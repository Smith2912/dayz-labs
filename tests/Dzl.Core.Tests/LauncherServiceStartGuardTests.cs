using Dzl.Core.App;
using Dzl.Core.Config;
using Dzl.Core.Launch;
using FluentAssertions;

public class LauncherServiceStartGuardTests
{
    private static string Seed()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "config.json");
        GlobalStore.Save(new GlobalConfig
        {
            ProjectsRoot = Path.Combine(dir, "projects"),
            DayzPath = Path.Combine(dir, "no-dayz"),
            AutoLaunchTray = false,
        }, path);
        Profiles.EnsureDefault(path);
        return path;
    }

    // Record the TEST RUNNER's own pid + image so StateFile.ReadLive sees a genuinely live entry.
    private static void MarkLive(string configPath, string target)
    {
        var pid = Environment.ProcessId;
        var image = ProcessManager.ImageOf(pid);
        image.Should().NotBeNull("the test host must be visible to tasklist");
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        StateFile.Write(configPath, target, pid, "debug", "test", image!,
            current.StartTime.ToUniversalTime(), current.MainModule!.FileName);
    }

    [Fact]
    public void Second_start_of_a_live_target_is_refused()
    {
        var path = Seed();
        MarkLive(path, "server");
        var svc = new LauncherService(path);

        var r = svc.StartTarget("server", "debug");

        r.Ok.Should().BeFalse();
        r.Message.Should().Contain("already up").And.Contain(Environment.ProcessId.ToString());
        svc.Start("debug", client: false).Ok.Should().BeFalse();
    }

    [Fact]
    public void Live_client_blocks_client_start_but_not_the_server()
    {
        var path = Seed();
        MarkLive(path, "client");
        var svc = new LauncherService(path);

        svc.StartTarget("client", "debug").Ok.Should().BeFalse();
        // Server start proceeds past the guard and fails only on the fake DayzPath spawn.
        svc.StartTarget("server", "debug").Message.Should().NotContain("already up");
    }

    [Fact]
    public void Unverified_legacy_record_blocks_a_start()
    {
        var path = Seed();
        // A legacy record cannot establish ownership, even when its image does not match.
        StateFile.Write(path, "server", Environment.ProcessId, "debug", "test", "DefinitelyNotThisImage.exe");
        var svc = new LauncherService(path);

        svc.StartTarget("server", "debug").Message.Should().Contain("unverified ownership");
        StateFile.ReadRaw(path).Should().ContainKey("server");
    }
}
