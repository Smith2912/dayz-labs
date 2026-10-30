using System.Text.Json;
using Dzl.Core.Json;
using Dzl.Core.Ipc;

namespace Dzl.Core.Launch;

public sealed record ProcInfo(int Pid, string Mode, string Source, string Exe,
    DateTime? StartedUtc = null, string? ExecutablePath = null);

public static class StateFile
{
    private static readonly JsonSerializerOptions Json = DzlJson.SnakeIndented;

    public static string Path(string configPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(IpcContract.CanonicalConfigPath(configPath))!,
            ".dzl-procs." + IpcContract.PipeNameFor(configPath)["dzl-ipc-v2-".Length..] + ".json");

    private static string LegacyPath(string configPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(IpcContract.CanonicalConfigPath(configPath))!, ".dzl-procs.json");

    public static IDisposable AcquireLock(string configPath)
    {
        var mutex = new Mutex(false, "dzl-process-" + IpcContract.PipeNameFor(configPath)["dzl-ipc-v2-".Length..]);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException("Timed out waiting for the process lock."); }
            catch (AbandonedMutexException) { /* acquired after an interrupted owner */ }
            return new HeldMutex(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public static Dictionary<string, ProcInfo> ReadRaw(string configPath)
    {
        var p = Path(configPath);
        if (!File.Exists(p)) p = LegacyPath(configPath);
        if (!File.Exists(p)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, ProcInfo>>(File.ReadAllText(p), Json)
                ?? throw new JsonException("Process ownership state must be an object."); }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new InvalidDataException($"Process ownership state at '{p}' could not be read; it was preserved.", ex); }
    }

    // imageOf(pid) -> image name or null if not running (injectable for tests).
    public static Dictionary<string, ProcInfo> ReadLive(string configPath, Func<int, string?> imageOf)
    {
        using var guard = AcquireLock(configPath);
        var raw = ReadRaw(configPath);
        var live = raw.Where(kv =>
        {
            if (kv.Value.StartedUtc is null || string.IsNullOrEmpty(kv.Value.ExecutablePath))
                return true; // legacy ownership is unknown; do not erase or reuse it automatically
            var identity = ProcessManager.InspectIdentity(kv.Value);
            if (identity == ProcessIdentity.Gone) return false;
            if (identity != ProcessIdentity.Owned) return true; // unknown or PID reused: retain for recovery
            return true;
        }).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (live.Count != raw.Count) WriteAll(configPath, live);
        return live;
    }

    public static void Write(string configPath, string target, int pid, string mode, string source, string exe,
        DateTime? startedUtc = null, string? executablePath = null)
    {
        using var guard = AcquireLock(configPath);
        var data = ReadRaw(configPath);
        data[target] = new ProcInfo(pid, mode, source, exe, startedUtc, executablePath);
        WriteAll(configPath, data);
    }

    /// <summary>Persist a fail-closed reservation before starting a process.</summary>
    public static void Reserve(string configPath, string target, string mode, string source, string exe)
    {
        using var guard = AcquireLock(configPath);
        var data = ReadRaw(configPath);
        if (data.ContainsKey(target)) throw new InvalidOperationException($"{target} already has an ownership record; resolve it before starting.");
        data[target] = new ProcInfo(0, mode, source, exe);
        WriteAll(configPath, data);
    }

    public static void Clear(string configPath, string target)
    {
        using var guard = AcquireLock(configPath);
        var data = ReadRaw(configPath);
        if (data.Remove(target)) WriteAll(configPath, data);
    }

    private static void WriteAll(string configPath, Dictionary<string, ProcInfo> data)
    {
        var path = Path(configPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Atomic swap so a concurrent reader (tray poll vs CLI/MCP) never sees a half-written file.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json)); File.Move(tmp, path, overwrite: true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
