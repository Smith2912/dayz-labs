using System.Diagnostics;
using Dzl.Core.Config;
using Dzl.Core.Economy;
using Dzl.Core.Env;
using Dzl.Core.Procs;

namespace Dzl.Core.Launch;

public enum ProcessIdentity { Owned, Gone, Mismatch, Unknown }

public static class ProcessManager
{
    public static ProcessIdentity InspectIdentity(ProcInfo info)
    {
        if (info.StartedUtc is null || string.IsNullOrWhiteSpace(info.ExecutablePath)) return ProcessIdentity.Unknown;
        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.HasExited) return ProcessIdentity.Gone;
            var actualPath = process.MainModule?.FileName;
            if (actualPath is null) return ProcessIdentity.Unknown;
            return process.StartTime.ToUniversalTime() == info.StartedUtc.Value.ToUniversalTime() &&
                   string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(info.ExecutablePath), StringComparison.OrdinalIgnoreCase)
                ? ProcessIdentity.Owned : ProcessIdentity.Mismatch;
        }
        catch (ArgumentException) { return ProcessIdentity.Gone; }
        catch (InvalidOperationException) { return ProcessIdentity.Gone; }
        catch { return ProcessIdentity.Unknown; }
    }
    // Parse one line of `tasklist /FI "PID eq N" /NH /FO CSV`. First quoted
    // field is the image name. INFO:/empty -> null (not running).
    public static string? ParseImage(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = line.Split("\",\"");
        var first = parts[0].TrimStart('"').TrimEnd('"');
        return first.Length == 0 ? null : first;
    }

    /// <summary>Image name of a live PID via <c>tasklist</c>, or null when the PID is gone or the
    /// lookup fails. Never throws — this runs on every status call from every frontend.</summary>
    public static string? ImageOf(int pid)
    {
        var r = ProcRunner.Run("tasklist", new[] { "/FI", $"PID eq {pid}", "/NH", "/FO", "CSV" },
            new RunOpts(TimeoutMs: 10_000));
        return r.Ok ? ParseImage(r.StdOut) : null;
    }

    public static string ServerExe(DzlConfig c, string mode) => mode == "debug" ? c.ExeDebug : c.ExeNormal;
    public static string ClientExe(DzlConfig c, string mode) => mode == "debug" ? c.ClientExeDebug : c.ClientExeNormal;

    /// <summary>Install dir for the dedicated server (normal mode). Falls back to <see cref="DzlConfig.DayzPath"/>.</summary>
    public static string ServerInstallPath(DzlConfig c) =>
        string.IsNullOrWhiteSpace(c.DayzServerPath) ? c.DayzPath : c.DayzServerPath.Trim();

    public static Process Spawn(string mode, string target, DzlConfig cfg, string source = "cli", string? configPath = null, bool connect = true)
    {
        using var custody = configPath is null ? null : StateFile.AcquireLock(configPath);
        var exe = target == "server" ? ServerExe(cfg, mode) : ClientExe(cfg, mode);
        var installDir = target == "server" && mode == "normal" ? ServerInstallPath(cfg) : cfg.DayzPath;
        // Point the instance's serverDZ.cfg template at its own mission (absolute path) — the engine forces
        // $currentdir to the exe dir, so a bare template name would load the install's mission instead.
        if (target == "server" && MissionLocator.Resolve(cfg)?.MissionDir is { } missionDir)
            ServerScaffold.EnsureAbsoluteTemplate(cfg.ConfigName, missionDir);
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(installDir, exe),
            // Server with an absolute instance serverDZ.cfg runs from the instance dir so DayZ resolves
            // its -config + mission there (it rejects absolute -config). Client always from the install.
            WorkingDirectory = ArgvBuilder.WorkingDir(cfg, target, mode),
            UseShellExecute = false,
        };
        foreach (var a in ArgvBuilder.Build(mode, target, cfg, connect)) psi.ArgumentList.Add(a);
        // Reserve the target durably before starting. An interruption after launch
        // leaves an unknown record that blocks another start until it is resolved.
        if (configPath is not null) StateFile.Reserve(configPath, target, mode, source, exe);
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}"); }
        catch
        {
            if (configPath is not null) StateFile.Clear(configPath, target);
            throw;
        }
        if (configPath is not null)
        {
            try
            {
                var started = proc.StartTime.ToUniversalTime();
                var actual = proc.MainModule?.FileName ?? Path.GetFullPath(psi.FileName);
                StateFile.Write(configPath, target, proc.Id, mode, source, exe, started, actual);
            }
            catch (Exception ex)
            {
                var pid = proc.Id;
                try
                {
                    if (!proc.HasExited) { proc.Kill(entireProcessTree: true); if (!proc.WaitForExit(10_000)) throw new TimeoutException("process did not exit"); }
                }
                catch (Exception cleanup)
                {
                    throw new InvalidOperationException($"Process ownership could not be saved and cleanup could not be confirmed. Recovery required for PID {pid}: {cleanup.Message}", ex);
                }
                try { StateFile.Clear(configPath, target); }
                catch (Exception clear)
                {
                    throw new InvalidOperationException($"Spawned PID {pid} was stopped, but its reservation could not be cleared. Recovery is required: {clear.Message}", ex);
                }
                throw new InvalidOperationException($"Process ownership could not be saved; spawned PID {pid} was stopped.", ex);
            }
        }
        return proc;
    }

    public static void Stop(string target, DzlConfig cfg, string configPath)
    {
        var info = StateFile.ReadRaw(configPath).GetValueOrDefault(target);
        if (info is not null)
        {
            var identity = InspectIdentity(info);
            if (identity == ProcessIdentity.Owned)
            {
                using var process = Process.GetProcessById(info.Pid);
                // Recheck through the same process object immediately before signaling.
                if (process.StartTime.ToUniversalTime() != info.StartedUtc!.Value.ToUniversalTime() ||
                    !string.Equals(process.MainModule?.FileName, info.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"PID {info.Pid} changed identity; no process was stopped.");
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(10_000)) throw new TimeoutException($"PID {info.Pid} did not exit; ownership was retained.");
            }
            else if (identity != ProcessIdentity.Gone)
                throw new InvalidOperationException($"Cannot verify ownership of PID {info.Pid} ({identity}); no process was stopped. Resolve the saved record manually.");
        }
        StateFile.Clear(configPath, target);
    }

    public static Process Restart(string mode, DzlConfig cfg, string configPath, string source = "cli")
    {
        Stop("server", cfg, configPath);
        return Spawn(mode, "server", cfg, source, configPath);
    }
}
