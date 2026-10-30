using System.Text.Json;
using Dzl.Core.Procs;
using Dzl.Core.Build;

namespace Dzl.Core.Tools;

/// <summary>One unpacked PBO's progress line.</summary>
public sealed record UnpackItem(int Index, int Total, string Pbo, string Status);

/// <summary>Outcome of a full game-data unpack.</summary>
public sealed record UnpackResult(bool Ok, int Total, int Extracted, int Skipped, int Failed, string Message);

/// <summary>
/// Reliable vanilla game-data extraction: drives DayZ Tools' <c>BankRev.exe</c> over EVERY game PBO, unpacking
/// each faithfully to the project drive at its own prefix (<c>BankRev -f P:\ -prefix &lt;pbo&gt;</c>). This is the
/// drop-in for <c>WorkDrive.exe /ExtractGameData</c>, whose built-in extraction is unreliable — it creates the
/// folders but silently skips files (e.g. <c>dz\worlds\enoch\data\enoch_riversea.emat</c>), which then read as
/// "missing" to preflight/binarize even though they exist in the game's PBOs. Unpacking each PBO individually with
/// BankRev avoids that. Incremental by default (a manifest skips PBOs already extracted at their current
/// timestamp+size AND verified destination digests); <c>force</c> re-extracts everything.
/// </summary>
public static class GameUnpack
{
    /// <summary>The manifest of what's been extracted, kept at the destination root so it travels with the drive.</summary>
    public const string ManifestName = ".dzl-extracted.json";

    /// <summary><c>-f &lt;dest&gt; -prefix &lt;pbo&gt;</c> — extract the PBO under <paramref name="destRoot"/> at its
    /// own PBO prefix (so <c>worlds_enoch.pbo</c> lands at <c>&lt;dest&gt;\dz\worlds\enoch\…</c>). Pure.</summary>
    public static List<string> BankRevArgs(string pbo, string destRoot) =>
        new() { "-f", destRoot, "-prefix", pbo };

    /// <summary>Identity stamp for a PBO: last-write + size. Re-extract only when this changes. Pure.</summary>
    public static string Stamp(FileInfo pbo) => $"{pbo.LastWriteTimeUtc.Ticks}:{pbo.Length}";

    /// <summary>Every VANILLA <c>*.pbo</c> under the DayZ install (Addons, sakhal\Addons, dta, …), sorted —
    /// EXCLUDING mod/workshop content. Subscribed Steam mods live under <c>!Workshop\@&lt;mod&gt;</c> (and loose
    /// <c>@&lt;mod&gt;</c> folders), often as junctions that <c>EnumerateFiles</c> follows; without this filter a
    /// machine with many mods reports thousands of PBOs (e.g. 3600+) and would unpack every mod into P:. We only
    /// want game data, so any path segment starting with <c>@</c> or <c>!</c> is skipped.</summary>
    public static IReadOnlyList<string> FindGamePbos(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return Array.Empty<string>();
        try
        {
            var root = Path.GetFullPath(gameDir);
            return Directory.EnumerateFiles(root, "*.pbo", SearchOption.AllDirectories)
                .Where(p => !IsUnderModFolder(root, p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>True when any folder on <paramref name="pbo"/>'s path under <paramref name="root"/> starts with
    /// <c>@</c> or <c>!</c> — the convention for mods / Steam Workshop (<c>!Workshop</c>), which are NOT vanilla.</summary>
    public static bool IsUnderModFolder(string root, string pbo)
    {
        var rel = Path.GetRelativePath(root, pbo);
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (seg.Length > 0 && (seg[0] == '@' || seg[0] == '!')) return true;
        return false;
    }

    /// <summary>Unpack every game PBO into <paramref name="destRoot"/> (the mounted project drive, e.g. <c>P:\</c>).
    /// Reports each PBO via <paramref name="onItem"/>; honours <paramref name="cancelled"/> between PBOs.</summary>
    public static UnpackResult UnpackAll(string bankRevExe, string gameDir, string destRoot, bool force,
        Action<UnpackItem>? onItem = null, Func<bool>? cancelled = null)
    {
        using var paths = new NativePathScope();
        return UnpackAllCore(bankRevExe, gameDir, destRoot, force,
            (pbo, dest) => ProcRunner.Run(bankRevExe, BankRevArgs(paths.FilePath(pbo), paths.DirectoryPath(dest)),
                new RunOpts(WorkingDir: Path.GetDirectoryName(bankRevExe), TimeoutMs: 10 * 60 * 1000)).Ok,
            onItem, cancelled);
    }

    // Injection covers orchestration/integrity with disposable archives, without invoking native tools.
    internal static UnpackResult UnpackAllCore(string bankRevExe, string gameDir, string destRoot, bool force,
        Func<string, string, bool> extract, Action<UnpackItem>? onItem = null, Func<bool>? cancelled = null)
    {
        if (string.IsNullOrWhiteSpace(bankRevExe) || !File.Exists(bankRevExe))
            return new UnpackResult(false, 0, 0, 0, 0, "BankRev.exe not found — set the DayZ Tools path");
        if (!Directory.Exists(destRoot))
            return new UnpackResult(false, 0, 0, 0, 0, $"destination not available: {destRoot} (mount P: first)");

        var pbos = FindGamePbos(gameDir);
        if (pbos.Count == 0)
            return new UnpackResult(false, 0, 0, 0, 0, $"no PBOs found under {gameDir}");

        var manifestPath = Path.Combine(destRoot, ManifestName);
        var manifest = LoadManifest(manifestPath);

        int extracted = 0, skipped = 0, failed = 0;
        for (var i = 0; i < pbos.Count; i++)
        {
            if (cancelled?.Invoke() == true)
                return new UnpackResult(false, pbos.Count, extracted, skipped, failed,
                    $"cancelled after {extracted + skipped + failed}/{pbos.Count}");

            var pbo = pbos[i];
            var idx = i + 1;
            var stamp = Stamp(new FileInfo(pbo));
            if (!force && manifest.TryGetValue(pbo, out var prev) && prev.Stamp == stamp && VerifyOutputs(destRoot, prev))
            {
                skipped++;
                onItem?.Invoke(new UnpackItem(idx, pbos.Count, pbo, "up to date"));
                continue;
            }

            bool ran;
            OutputReceipt? receipt = null;
            try
            {
                // Validate destinations before handing the archive to an external extractor.
                _ = OutputPaths(pbo, destRoot);
                ran = extract(pbo, destRoot);
                if (ran) receipt = CaptureOutputs(pbo, destRoot, stamp);
            }
            catch { ran = false; }
            if (ran && receipt is not null)
            {
                extracted++;
                manifest[pbo] = receipt;
                onItem?.Invoke(new UnpackItem(idx, pbos.Count, pbo, "extracted"));
                SaveManifest(manifestPath, manifest);   // persist incrementally so a crash/cancel keeps progress
            }
            else
            {
                failed++;
                manifest.Remove(pbo);
                SaveManifest(manifestPath, manifest);
                onItem?.Invoke(new UnpackItem(idx, pbos.Count, pbo, "FAILED: extraction or destination integrity check failed"));
            }
        }

        var ok = failed == 0;
        return new UnpackResult(ok, pbos.Count, extracted, skipped, failed,
            $"{(ok ? "done" : "completed with errors")}: {extracted} extracted, {skipped} up-to-date, {failed} failed");
    }

    private sealed record OutputReceipt(string Stamp, Dictionary<string, string> Digests);

    private static List<(string Path, long Size)> OutputPaths(string pbo, string destRoot)
    {
        var header = PboHeader.Read(pbo) ?? throw new InvalidDataException("Unreadable PBO header");
        if (header.Entries.Count == 0) throw new InvalidDataException("Empty PBO output manifest");
        var root = Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return header.Entries.Select(entry =>
        {
            var relative = Path.Combine(header.Prefix.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
                entry.Name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            var output = Path.GetFullPath(Path.Combine(root, relative));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PBO output outside destination");
            return (output, (long)(entry.PackingMethod == 0 ? entry.DataSize : entry.OriginalSize));
        }).ToList();
    }

    private static OutputReceipt CaptureOutputs(string pbo, string destRoot, string stamp)
    {
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, size) in OutputPaths(pbo, destRoot))
        {
            if (!File.Exists(path) || new FileInfo(path).Length != size)
                throw new InvalidDataException($"Extracted file missing or wrong length: {path}");
            digests[Path.GetRelativePath(destRoot, path)] = BuildCache.OutputDigest(path);
        }
        return new OutputReceipt(stamp, digests);
    }

    private static bool VerifyOutputs(string destRoot, OutputReceipt receipt)
    {
        try
        {
            if (receipt.Digests is null || receipt.Digests.Count == 0) return false;
            var root = Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return receipt.Digests.All(item =>
            {
                var path = Path.GetFullPath(Path.Combine(root, item.Key));
                return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) &&
                    BuildCache.OutputDigest(path) == item.Value;
            });
        }
        catch { return false; }
    }

    private static Dictionary<string, OutputReceipt> LoadManifest(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, OutputReceipt>>(File.ReadAllText(path));
                if (d is not null) return new Dictionary<string, OutputReceipt>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* corrupt/unreadable manifest — treat as empty (everything re-extracts) */ }
        return new Dictionary<string, OutputReceipt>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveManifest(string path, Dictionary<string, OutputReceipt> manifest)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* best-effort — next run just re-extracts */ }
    }
}
