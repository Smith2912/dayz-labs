namespace Dzl.Core.Economy;

public sealed record CeSnapshot(string Id, DateTime Created, string Path);

/// <summary>Versioned snapshots of any Central Economy file. Keeps the newest <see cref="Keep"/>.
/// On-disk layout is intentionally compatible with <see cref="TypesBackup"/>: backups land in
/// <c>.dzl-&lt;stem&gt;-backups\</c> next to the file, named <c>&lt;stem&gt;.&lt;id&gt;&lt;ext&gt;</c>.
/// For <c>types.xml</c> this produces the same folder and filenames as TypesBackup used to, so
/// existing user backups remain visible after the migration.
/// Never throws — best-effort I/O.</summary>
public static class CeBackup
{
    public const int Keep = 20;

    private static int _seq;   // uniquifier so two snapshots within the same millisecond don't collide

    /// <summary>Serialize cooperating dzl writers for one canonical CE path.</summary>
    public static IDisposable LockFiles(IEnumerable<string> files)
    {
        var locks = new List<Mutex>();
        try
        {
            foreach (var path in files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
                var mutex = new Mutex(false, "dzl-ce-" + hash);
                try
                {
                    try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException("Timed out waiting for the CE file lock."); }
                    catch (AbandonedMutexException) { /* lock acquired after an interrupted writer */ }
                    locks.Add(mutex);
                }
                catch { mutex.Dispose(); throw; }
            }
            return new FileLocks(locks);
        }
        catch
        {
            foreach (var mutex in locks.AsEnumerable().Reverse()) { mutex.ReleaseMutex(); mutex.Dispose(); }
            throw;
        }
    }

    private sealed class FileLocks(List<Mutex> locks) : IDisposable
    {
        public void Dispose()
        {
            foreach (var mutex in locks.AsEnumerable().Reverse()) { mutex.ReleaseMutex(); mutex.Dispose(); }
            locks.Clear();
        }
    }

    internal static string BackupDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return Path.Combine(dir, $".dzl-{stem}-backups");
    }

    private static string Stem(string filePath) => Path.GetFileNameWithoutExtension(filePath);
    private static string Ext(string filePath)  => Path.GetExtension(filePath);

    /// <summary>Snapshot the current file (if any). Prunes the backup folder to <see cref="Keep"/> afterwards.</summary>
    public static void Snapshot(string filePath)
    {
        if (!File.Exists(filePath)) return;
        if (SnapshotWithId(filePath, id: null) == "")
            throw new IOException($"Could not create a backup of '{filePath}'; the file was not saved.");
    }

    /// <summary>Stage a complete replacement next to the target before replacing it.</summary>
    public static void WriteTextAtomically(string filePath, string text)
    {
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, filePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Snapshot with a caller-supplied id (used by <see cref="TypesBackup"/> for deterministic test stamps).
    /// When <paramref name="id"/> is null a unique timestamped id is generated.</summary>
    internal static string SnapshotWithId(string filePath, string? id, bool prune = true)
    {
        try
        {
            if (!File.Exists(filePath)) return "";
            var dir = BackupDir(filePath);
            Directory.CreateDirectory(dir);

            if (id is null)
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                var n = System.Threading.Interlocked.Increment(ref _seq);
                id = $"{stamp}-{n}";
            }

            var dst = Path.Combine(dir, $"{Stem(filePath)}.{id}{Ext(filePath)}");
            // Avoid clobbering when caller reuses the same id within a test.
            for (var i = 1; File.Exists(dst); i++)
                dst = Path.Combine(dir, $"{Stem(filePath)}.{id}-{i}{Ext(filePath)}");

            File.Copy(filePath, dst);
            if (prune) Prune(filePath, dir);
            return dst;
        }
        catch { return ""; }
    }

    /// <summary>All snapshots for <paramref name="filePath"/>, newest first.</summary>
    public static List<CeSnapshot> List(string filePath)
    {
        try
        {
            var dir = BackupDir(filePath);
            if (!Directory.Exists(dir)) return new();
            var stem = Stem(filePath);
            var ext  = Ext(filePath);
            var prefix = $"{stem}.";
            return Directory.EnumerateFiles(dir, $"{prefix}*{ext}")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .Select(f =>
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(f);
                    var id = nameNoExt[prefix.Length..];
                    var created = File.GetLastWriteTime(f);
                    return new CeSnapshot(id, created, f);
                })
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Restore a snapshot (by id) over the live file.
    /// Snapshots the current file first so the restore is undoable.
    /// Returns false when the id is not found or an error occurs.</summary>
    public static bool Restore(string filePath, string id)
    {
        try
        {
            using var guard = LockFiles(new[] { filePath });
            var dir = BackupDir(filePath);
            var backupFile = Path.Combine(dir, $"{Stem(filePath)}.{id}{Ext(filePath)}");
            if (!File.Exists(backupFile)) return false;
            // Read the selected point before making a pre-restore snapshot. Pruning
            // cannot remove it while a restore is in progress.
            var selected = File.ReadAllBytes(backupFile);
            var previous = File.Exists(filePath) ? SnapshotWithId(filePath, id: null, prune: false) : "";
            if (File.Exists(filePath) && previous == "") return false;
            var tmp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, selected);
                File.Move(tmp, filePath, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            Prune(filePath, dir, new[] { backupFile, previous }.Where(p => p.Length > 0));
            return true;
        }
        catch { return false; }
    }

    private static void Prune(string filePath, string dir, IEnumerable<string>? protectedFiles = null)
    {
        var stem   = Stem(filePath);
        var ext    = Ext(filePath);
        var files  = Directory.EnumerateFiles(dir, $"{stem}.*{ext}")
            .OrderByDescending(f => File.GetLastWriteTime(f)).ToList();
        var keep = (protectedFiles ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (keep.Count >= Keep) break;
            keep.Add(file);
        }
        foreach (var old in files.Where(f => !keep.Contains(f)))
            try { File.Delete(old); } catch { /* best-effort */ }
    }
}
