using System.Xml.Linq;
using System.Security.Cryptography;
using Dzl.Core.Config;
using Dzl.Core.Economy;
using Dzl.Core.Economy.Lint;

namespace Dzl.Core.App;

/// <summary>One Types entry plus the origin/source of the CE file it came from (for source-aware
/// listing/filtering in the CLI/MCP/tray).</summary>
public sealed record TypeRow(TypeEntry Entry, CeOrigin Origin, string ModSource);
public sealed record TypesSnapshot(string? Primary, IReadOnlyDictionary<string, string> Revisions,
    IReadOnlyList<TypeRow> Rows, IReadOnlyList<string> Errors);

/// <summary>
/// Central Economy editor over ALL Types files of the active mission — the vanilla <c>db\types.xml</c>
/// plus every <c>type="types"</c> file referenced from <c>cfgeconomycore.xml</c>.
/// </summary>
/// <remarks>Reads union entries (each stamped with its <see cref="TypeEntry.SourceFile"/>); writes
/// route each entry back to its own file. Every write snapshots a versioned backup
/// (<see cref="CeBackup"/>) and edits in place so comments/order survive a round-trip.</remarks>
public sealed class TypesService
{
    private readonly string _configPath;
    public TypesService(string configPath) { _configPath = configPath; }

    private MissionPaths? Mission()
    {
        var (cfg, _, _) = Profiles.ResolveActive(_configPath);
        return MissionLocator.Resolve(cfg);
    }

    private List<CeFileRef> ResolveTypesFiles() => ResolveAll().Files;

    // Primary = vanilla db/types.xml when present, else the first resolved file.
    private (List<CeFileRef> Files, string? Primary) ResolveAll()
    {
        var mp = Mission();
        var files = new List<CeFileRef>();
        if (mp is not null)
        {
            if (File.Exists(mp.EconomyCore))
            {
                try
                {
                    files.AddRange(EconomyCore.Parse(File.ReadAllText(mp.EconomyCore), mp.MissionDir)
                        .Where(r => r.Kind == CeKind.Types));
                }
                catch { /* malformed cfgeconomycore.xml → fall back to vanilla only */ }
            }

            if (mp.Vanilla is not null &&
                !files.Any(r => string.Equals(r.Path, mp.Vanilla, StringComparison.OrdinalIgnoreCase)))
            {
                files.Insert(0, new CeFileRef(mp.Vanilla, CeKind.Types, CeOrigin.Vanilla, "vanilla"));
            }

            files = files.Where(r => File.Exists(r.Path)).ToList();
        }

        string? primary = mp?.Vanilla ?? files.FirstOrDefault()?.Path;
        return (files, primary);
    }

    /// <summary>The active mission's primary types file (vanilla db\types.xml, or the first resolved
    /// Types file). Kept for back-compat with callers that probe a single "the types file".</summary>
    public string? TypesFile() => ResolveAll().Primary;

    /// <summary>Union of every resolved Types file's entries, each stamped with its source file path.
    /// Per-file parse errors are skipped.</summary>
    public List<TypeEntry> List() => Rows().Select(r => r.Entry).ToList();

    /// <summary>Like <see cref="List"/> but each entry carries the origin + mod source of its file.</summary>
    public List<TypeRow> Rows()
        => LoadSnapshot().Rows.ToList();

    /// <summary>Capture the successfully parsed file set and exact byte revisions for a later save.</summary>
    public TypesSnapshot LoadSnapshot()
    {
        var rows = new List<TypeRow>();
        var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var (files, primary) = ResolveAll();
        foreach (var fileRef in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(fileRef.Path);
                using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
                var entries = TypesXml.Parse(reader.ReadToEnd(), fileRef.Path);
                revisions.Add(fileRef.Path, Convert.ToHexString(SHA256.HashData(bytes)));
                foreach (var e in entries) rows.Add(new TypeRow(e, fileRef.Origin, fileRef.ModSource));
            }
            catch (Exception ex) { errors.Add($"{fileRef.Path}: {ex.Message}"); }
        }
        return new TypesSnapshot(primary, revisions, rows, errors);
    }

    /// <summary>Valid usage/value/tag/category names from the mission's <c>cfglimitsdefinition.xml</c>
    /// (<see cref="LimitsDef.Empty"/> when absent).</summary>
    public LimitsDef Limits()
    {
        var mp = Mission();
        if (mp is null) return LimitsDef.Empty;
        var path = Path.Combine(mp.MissionDir, "cfglimitsdefinition.xml");
        if (!File.Exists(path)) return LimitsDef.Empty;
        try { return LimitsXml.Parse(File.ReadAllText(path)); }
        catch { return LimitsDef.Empty; }
    }

    /// <summary>Named combos from the mission's <c>cfglimitsdefinitionuser.xml</c> (empty when absent).
    /// A combo name is a valid usage/value reference the engine expands, so it must count as known in lint.</summary>
    public IReadOnlyList<LimitsUserGroup> Combos()
    {
        var mp = Mission();
        if (mp is null) return System.Array.Empty<LimitsUserGroup>();
        var path = Path.Combine(mp.MissionDir, "cfglimitsdefinitionuser.xml");
        if (!File.Exists(path)) return System.Array.Empty<LimitsUserGroup>();
        try { return LimitsUserXml.Parse(File.ReadAllText(path)); }
        catch { return System.Array.Empty<LimitsUserGroup>(); }
    }

    /// <summary>Run the CE lint rules over the full multi-file Types set against the mission's limits, with
    /// named combos folded in so a combo reference (e.g. <c>usage="TownVillage"</c>) isn't flagged unknown.</summary>
    public IReadOnlyList<LintFinding> Lint() =>
        new LintEngine().Run(new CeFileSet(List()), Limits().WithCombos(Combos()));

    /// <summary>Sync every resolved Types file to the edited set: entries are grouped by their
    /// <see cref="TypeEntry.SourceFile"/> and each target file is pruned, upserted, snapshotted,
    /// and written back. Ok only when no file failed.</summary>
    /// <remarks>An empty SourceFile routes to the primary file. A non-empty path outside the
    /// loaded file set is rejected so stale rows cannot be written to a different mission.</remarks>
    public OpResult SaveAll(IReadOnlyList<TypeEntry> entries) => SaveAll(entries, LoadSnapshot());

    public OpResult SaveAll(IReadOnlyList<TypeEntry> entries, TypesSnapshot snapshot)
        => SaveAll(entries, snapshot, out _);

    /// <summary>Returns revisions of files actually committed, including after a partial failure, so a draft can retry.</summary>
    public OpResult SaveAll(IReadOnlyList<TypeEntry> entries, TypesSnapshot snapshot, out TypesSnapshot updatedSnapshot)
    {
        updatedSnapshot = snapshot;
        using var configGuard = Dzl.Core.Launch.StateFile.AcquireLock(_configPath);
        var (resolved, primary) = ResolveAll();
        if (primary is null) return new(false, "no types.xml files resolved");
        if (snapshot.Errors.Count > 0) return new(false, "some types files could not be loaded; no files were saved");
        if (!string.Equals(primary, snapshot.Primary, StringComparison.OrdinalIgnoreCase) ||
            !new HashSet<string>(resolved.Select(f => f.Path), StringComparer.OrdinalIgnoreCase).SetEquals(snapshot.Revisions.Keys))
            return new(false, "types file set or active mission changed; reload before saving");
        if (entries.Any(e => !string.IsNullOrEmpty(e.SourceFile) && !snapshot.Revisions.ContainsKey(e.SourceFile)))
            return new(false, "a row refers to a file outside the loaded types set");
        IDisposable fileLocks;
        try { fileLocks = CeBackup.LockFiles(snapshot.Revisions.Keys); }
        catch (Exception ex) { return new(false, $"could not lock types files: {ex.Message}"); }
        using var guard = fileLocks;
        try
        {
            foreach (var (file, revision) in snapshot.Revisions)
                if (!File.Exists(file) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != revision)
                    return new(false, $"{Path.GetFileName(file)} changed since it was loaded; reload before saving");
        }
        catch (Exception ex) { return new(false, $"could not verify loaded types files: {ex.Message}"); }

        var valid = new HashSet<string>(snapshot.Revisions.Keys, StringComparer.OrdinalIgnoreCase);
        var groups = entries.GroupBy(e =>
            string.IsNullOrEmpty(e.SourceFile) ? primary
            : valid.Contains(e.SourceFile) ? e.SourceFile
            : primary,
            StringComparer.OrdinalIgnoreCase);

        var saved = new List<string>();
        var failed = new List<string>();
        var byFile = groups.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (byFile.Values.Any(rows => rows.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)))
            return new(false, "duplicate type names in a file; resolve name collisions before saving");
        var revisions = new Dictionary<string, string>(snapshot.Revisions, StringComparer.OrdinalIgnoreCase);
        foreach (var file in valid)
        {
            var g = byFile.TryGetValue(file, out var rows) ? rows : new List<TypeEntry>();
            try
            {
                var doc = File.Exists(file)
                    ? TypesXml.ParseDoc(File.ReadAllText(file))
                    : new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("types"));
                var keep = new HashSet<string>(g.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var t in doc.Root!.Elements("type").ToList())
                    if (!keep.Contains(t.Attribute("name")?.Value ?? "")) t.Remove();
                foreach (var e in g) TypesXml.Upsert(doc, e);
                var xml = TypesXml.ToXml(doc);
                var committedRevision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml)));
                CeBackup.Snapshot(file);
                CeBackup.WriteTextAtomically(file, xml);
                revisions[file] = committedRevision;
                saved.Add(Path.GetFileName(file));
            }
            catch { failed.Add(Path.GetFileName(file)); }
        }

        updatedSnapshot = snapshot with { Revisions = revisions };
        if (failed.Count == 0)
            return new(true, $"saved {entries.Count} types across {saved.Count} file(s)");
        if (saved.Count > 0)
            return new(false, $"partial save: {saved.Count} ok, failed: {string.Join(", ", failed)}");
        return new(false, $"failed: {string.Join(", ", failed)}");
    }

    /// <summary>Set/insert one type with nullable overrides (CLI/MCP). Writes to the entry's existing
    /// source file if the type already exists, else to <paramref name="file"/> if given, else the primary
    /// file. Unspecified fields keep their current value (or the default for a new entry).
    /// <para>Cooperating dzl writers serialize these edits; external editors do not share the lock.</para></summary>
    public OpResult Set(string name, int? nominal = null, int? min = null, int? lifetime = null,
                       int? restock = null, int? cost = null, string? category = null, string? file = null)
    {
        using var configGuard = Dzl.Core.Launch.StateFile.AcquireLock(_configPath);
        if (string.IsNullOrWhiteSpace(name)) return new(false, "type name required");

        var (resolved, primary) = ResolveAll();
        IDisposable fileLocks;
        try { fileLocks = CeBackup.LockFiles(resolved.Select(f => f.Path)); }
        catch (Exception ex) { return new(false, $"could not lock types files: {ex.Message}"); }
        using var guard = fileLocks;

        // Probe in resolved order — the first file containing the name wins. Parsed docs are kept
        // so the edit below reuses them; malformed files are skipped exactly like Rows() skips them.
        var probed = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        string? target = null;
        XElement? el = null;
        foreach (var fileRef in resolved)
        {
            XDocument candidate;
            try { candidate = TypesXml.ParseDoc(File.ReadAllText(fileRef.Path)); }
            catch { continue; }   // skip the bad file
            probed[fileRef.Path] = candidate;
            var match = candidate.Root?.Elements("type").ByName(name);
            if (match is null) continue;
            target = fileRef.Path;
            el = match;
            break;
        }

        // Target: the file the existing type came from, else the file param matched against the
        // resolved CE list (by basename or full path, so "mymod_types.xml" lands in the real
        // CE-registered file, not CWD), else the primary types file.
        if (target is null)
        {
            if (!string.IsNullOrEmpty(file))
            {
                var matched = resolved.FirstOrDefault(r =>
                    Path.GetFileName(r.Path).Equals(file, StringComparison.OrdinalIgnoreCase) ||
                    r.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
                target = matched?.Path ?? primary;
            }
            else
            {
                target = primary;
            }
        }

        if (string.IsNullOrEmpty(target)) return new(false, "no types.xml for the active server's mission");

        try
        {
            var doc = probed.TryGetValue(target, out var reused)
                ? reused
                : File.Exists(target)
                    ? TypesXml.ParseDoc(File.ReadAllText(target))
                    : new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("types"));
            var cur = el != null ? TypesXml.ReadType(el) : new TypeEntry { Name = name };
            var merged = cur with
            {
                Name = name,
                Nominal = nominal ?? cur.Nominal,
                Min = min ?? cur.Min,
                Lifetime = lifetime ?? cur.Lifetime,
                Restock = restock ?? cur.Restock,
                Cost = cost ?? cur.Cost,
                Category = category ?? cur.Category,
            };
            CeBackup.Snapshot(target);
            TypesXml.Upsert(doc, merged);
            CeBackup.WriteTextAtomically(target, TypesXml.ToXml(doc));
            return new(true, el != null ? $"updated {name}" : $"added {name}");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>Remove the named type from whichever resolved file(s) contain it (each affected file is
    /// snapshotted first).</summary>
    public OpResult Remove(string name)
    {
        using var configGuard = Dzl.Core.Launch.StateFile.AcquireLock(_configPath);
        var files = ResolveTypesFiles();
        if (files.Count == 0) return new(false, "no types.xml for the active server's mission");
        IDisposable fileLocks;
        try { fileLocks = CeBackup.LockFiles(files.Select(f => f.Path)); }
        catch (Exception ex) { return new(false, $"could not lock types files: {ex.Message}"); }
        using var guard = fileLocks;

        var removed = 0;
        var failed = new List<string>();
        foreach (var fileRef in files)
        {
            try
            {
                var doc = TypesXml.ParseDoc(File.ReadAllText(fileRef.Path));
                if (!TypesXml.Remove(doc, name)) continue;
                CeBackup.Snapshot(fileRef.Path);
                CeBackup.WriteTextAtomically(fileRef.Path, TypesXml.ToXml(doc));
                removed++;
            }
            catch { failed.Add(Path.GetFileName(fileRef.Path)); }
        }

        if (failed.Count > 0) return new(false, $"failed: {string.Join(", ", failed)}");
        return removed > 0 ? new(true, $"removed {name}") : new(false, $"no such type: {name}");
    }

    /// <summary>Lists versioned backups for the <b>primary</b> types file only.</summary>
    /// <remarks>Mod/custom CE files maintain their own snapshots in <c>.dzl-&lt;stem&gt;-backups/</c>
    /// next to each file; full multi-file backup browsing is a future sub-project.</remarks>
    public List<TypesBackupInfo> Backups()
    {
        var f = ResolveAll().Primary;
        return f is null ? new() : TypesBackup.List(f);
    }

    /// <summary>Restores the <b>primary</b> types file from a specific backup snapshot
    /// (see <see cref="Backups"/> for the primary-only scope).</summary>
    public OpResult Restore(string backupFile, string? expectedFile = null)
    {
        using var configGuard = Dzl.Core.Launch.StateFile.AcquireLock(_configPath);
        var f = ResolveAll().Primary;
        if (f is null) return new(false, "no types.xml for the active server's mission");
        if (expectedFile is not null && !string.Equals(f, expectedFile, StringComparison.OrdinalIgnoreCase))
            return new(false, "active mission changed; reload before restoring");
        if (Path.IsPathRooted(backupFile) && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(backupFile)),
                Path.GetFullPath(TypesBackup.BackupDir(f)), StringComparison.OrdinalIgnoreCase))
            return new(false, "backup belongs to a different mission or file");
        return TypesBackup.Restore(f, backupFile)
            ? new(true, $"restored {Path.GetFileName(backupFile)}")
            : new(false, "restore failed");
    }
}
