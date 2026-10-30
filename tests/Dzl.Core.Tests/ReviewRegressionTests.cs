using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dzl.Core.App;
using Dzl.Core.Build;
using Dzl.Core.Config;
using Dzl.Core.Economy;
using Dzl.Core.Ipc;
using Dzl.Core.Launch;
using FluentAssertions;

public class ReviewRegressionTests
{
    private static string Config()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var config = Path.Combine(root, "config.json");
        GlobalStore.Save(new GlobalConfig { ProjectsRoot = Path.Combine(root, "projects") }, config);
        return config;
    }

    private static string Mission(string config)
    {
        var instance = Profiles.InstanceDir("test", config);
        var mission = Path.Combine(instance, "mpmissions", "dayzOffline.chernarusplus");
        Directory.CreateDirectory(Path.Combine(mission, "db"));
        Profiles.Save(DzlConfig.Default() with { ConfigName = Path.Combine(instance, "serverDZ.cfg") }, "test", config);
        Profiles.SetActive("test", config);
        return mission;
    }

    [Fact]
    public void Purge_rejects_parent_and_unregistered_directories()
    {
        var config = Config();
        var root = GlobalStore.Load(config).ProjectsRoot;
        Directory.CreateDirectory(Path.Combine(root, "servers", "unregistered"));
        var sentinel = Path.Combine(root, "source.txt");
        File.WriteAllText(sentinel, "keep");
        Profiles.Delete("..", config, true).Should().BeFalse();
        Profiles.Delete(".", config, true).Should().BeFalse();
        Profiles.Delete("safe.", config, true).Should().BeFalse();
        Profiles.Delete("unregistered", config, true).Should().BeFalse();
        File.ReadAllText(sentinel).Should().Be("keep");
        Directory.Exists(Path.Combine(root, "servers", "unregistered")).Should().BeTrue();
        Profiles.Save(DzlConfig.Default(), "safe", config);
        Profiles.Delete("safe", config, true).Should().BeTrue();
        File.ReadAllText(sentinel).Should().Be("keep");
    }

    [Fact]
    public void Malformed_config_is_never_replaced_during_initialization()
    {
        var config = Config();
        const string bad = "{\"projects_root\": \"recover-me\"";
        File.WriteAllText(config, bad);
        var action = () => Profiles.EnsureDefault(config);
        action.Should().Throw<InvalidDataException>();
        File.ReadAllText(config).Should().Be(bad);
    }

    [Fact]
    public void Empty_types_are_saved_and_stale_snapshots_are_rejected()
    {
        var config = Config();
        var mission = Mission(config);
        var file = Path.Combine(mission, "db", "types.xml");
        File.WriteAllText(file, "<types><type name=\"Apple\"/></types>");
        var snapshot = new TypesService(config).LoadSnapshot();
        new TypesService(config).SaveAll(Array.Empty<TypeEntry>(), snapshot).Ok.Should().BeTrue();
        new TypesService(config).List().Should().BeEmpty();
        var stale = new TypesService(config).LoadSnapshot();
        File.WriteAllText(file, "<types><type name=\"External\"/></types>");
        new TypesService(config).SaveAll(Array.Empty<TypeEntry>(), stale).Ok.Should().BeFalse();
        File.ReadAllText(file).Should().Contain("External");
    }

    [Fact]
    public async Task Concurrent_types_saves_from_same_revision_do_not_overwrite_each_other()
    {
        var config = Config();
        var mission = Mission(config);
        var file = Path.Combine(mission, "db", "types.xml");
        File.WriteAllText(file, "<types><type name=\"Apple\"><nominal>1</nominal></type></types>");
        var snapshot = new TypesService(config).LoadSnapshot();
        var first = snapshot.Rows.Select(r => r.Entry with { Nominal = 2 }).ToArray();
        var second = snapshot.Rows.Select(r => r.Entry with { Nominal = 3 }).ToArray();
        var results = await Task.WhenAll(
            Task.Run(() => new TypesService(config).SaveAll(first, snapshot)),
            Task.Run(() => new TypesService(config).SaveAll(second, snapshot)));
        results.Count(r => r.Ok).Should().Be(1);
        results.Single(r => !r.Ok).Message.Should().Contain("changed since it was loaded");
        new TypesService(config).List().Single().Nominal.Should().BeOneOf(2, 3);
    }

    [Fact]
    public void Backup_failure_does_not_change_ce_file()
    {
        var config = Config();
        var mission = Mission(config);
        var file = Path.Combine(mission, "db", "globals.xml");
        const string original = "<variables><var name=\"Review\" type=\"0\" value=\"1\"/></variables>";
        File.WriteAllText(file, original);
        File.WriteAllText(Path.Combine(mission, "db", ".dzl-globals-backups"), "block directory");
        new GlobalsService(config).SetVar("Review", 0, "2").ok.Should().BeFalse();
        File.ReadAllText(file).Should().Be(original);
    }

    [Fact]
    public void Restore_oldest_backup_keeps_it_and_the_pre_restore_state()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(root, "types.xml");
        for (var i = 0; i < CeBackup.Keep; i++)
        {
            File.WriteAllText(file, $"<types n=\"{i}\"/>");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30).AddMinutes(i));
            CeBackup.Snapshot(file);
        }
        var oldest = CeBackup.List(file).Last();
        File.WriteAllText(file, "<types n=\"current\"/>");
        // File.Copy preserves timestamps: the pre-restore point must survive even
        // when the live file is older than every retained snapshot.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-90));
        CeBackup.Restore(file, oldest.Id).Should().BeTrue();
        File.ReadAllText(file).Should().Contain("n=\"0\"");
        File.Exists(oldest.Path).Should().BeTrue();
        CeBackup.List(file).Should().Contain(s => File.ReadAllText(s.Path).Contains("current"));
        CeBackup.List(file).Should().HaveCount(CeBackup.Keep);
    }

    [Fact]
    public void Pbo_does_not_ship_files_excluded_from_preflight()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "config.cpp"), "config");
        File.WriteAllText(Path.Combine(source, "asset.psd"), "private work");
        var output = Path.Combine(root, "test.pbo");
        PboWriter.Pack(source, output, "Test").Item1.Should().BeTrue();
        PboHeader.Read(output)!.Entries.Should().NotContain(e => e.Name == "asset.psd");
        PboHeader.Read(output)!.Entries.Should().Contain(e => e.Name == "config.cpp");
    }

    [Fact]
    public void Changing_only_prefix_changes_the_build_cache_key()
    {
        var source = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(source, "config.cpp"), "config");
        var marker = Path.Combine(source, "$PBOPREFIX$");
        File.WriteAllText(marker, "Review\\Old");
        var before = BuildCache.SettingsFingerprint(source, false, false, null, null, null, null);
        File.WriteAllText(marker, "Review\\New");
        var after = BuildCache.SettingsFingerprint(source, false, false, null, null, null, null);
        before.Should().NotBe(after);
    }

    [Fact]
    public void Cached_output_is_rejected_if_the_pbo_or_signature_changes()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var pbo = Path.Combine(dir, "test.pbo");
        var signature = pbo + ".key.bisign";
        File.WriteAllText(pbo, "pbo");
        File.WriteAllText(signature, "signature");
        var entry = new BuildCache.Entry("source-hash", pbo, DateTime.UtcNow,
            BuildCache.OutputDigest(pbo), BuildCache.OutputDigest(signature));
        BuildCache.CachedOutputMatches(entry, dir, true).Should().BeTrue();
        File.WriteAllText(signature, "other signature");
        BuildCache.CachedOutputMatches(entry, dir, true).Should().BeFalse();
        File.WriteAllText(pbo, "other pbo");
        BuildCache.CachedOutputMatches(entry, dir, false).Should().BeFalse();
        BuildCache.CachedOutputMatches(new BuildCache.Entry("old", pbo, DateTime.UtcNow), dir, false).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_global_updates_preserve_both_changes()
    {
        var config = Config();
        await Task.WhenAll(
            Task.Run(() => GlobalStore.Update(config, g => g with { DayzPath = "first" })),
            Task.Run(() => GlobalStore.Update(config, g => g with { EditorPath = "second" })));
        var result = GlobalStore.Load(config);
        result.DayzPath.Should().Be("first");
        result.EditorPath.Should().Be("second");
    }

    [Fact]
    public void Configurations_in_same_folder_use_different_automation_pipes()
    {
        var first = Config();
        var second = Path.Combine(Path.GetDirectoryName(first)!, "other.json");
        IpcContract.PipeNameFor(first).Should().NotBe(IpcContract.PipeNameFor(second));
        IpcContract.PipeNameFor(first).Should().Be(IpcContract.PipeNameFor(Path.GetFullPath(first)));
        StateFile.Path(first).Should().NotBe(StateFile.Path(second));
    }

    [Fact]
    public async Task Two_pipe_authorities_in_one_directory_route_to_their_own_configurations()
    {
        var first = Config();
        var parent = Path.GetDirectoryName(first)!;
        var second = Path.Combine(parent, "other.json");
        GlobalStore.Save(new GlobalConfig { ProjectsRoot = Path.Combine(parent, "other-projects"), AutoLaunchTray = false }, second);
        foreach (var (config, port) in new[] { (first, 2401), (second, 2501) })
        {
            Profiles.Save(DzlConfig.Default() with { Port = port }, "test", config);
            Profiles.SetActive("test", config);
        }
        var firstCalls = 0;
        var secondCalls = 0;
        using var cancellation = new CancellationTokenSource();
        var listenerA = new PipeServer(() => { Interlocked.Increment(ref firstCalls); return new LauncherService(first); }, IpcContract.PipeNameFor(first)).RunAsync(cancellation.Token);
        var listenerB = new PipeServer(() => { Interlocked.Increment(ref secondCalls); return new LauncherService(second); }, IpcContract.PipeNameFor(second)).RunAsync(cancellation.Token);
        try
        {
            var results = await Task.WhenAll(Task.Run(() => new ControlPlane(first).StatusJson()), Task.Run(() => new ControlPlane(second).StatusJson()));
            using var a = JsonDocument.Parse(results[0]);
            using var b = JsonDocument.Parse(results[1]);
            a.RootElement.GetProperty("port").GetInt32().Should().Be(2401);
            b.RootElement.GetProperty("port").GetInt32().Should().Be(2501);
            firstCalls.Should().Be(1);
            secondCalls.Should().Be(1);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(listenerA, listenerB).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void Stop_rejects_a_reused_pid_without_signaling_it()
    {
        var config = Config();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var image = Path.GetFileName(process.MainModule!.FileName);
        StateFile.Write(config, "server", process.Id, "debug", "test", image,
            process.StartTime.ToUniversalTime().AddSeconds(-1), process.MainModule.FileName);
        Action stop = () => ProcessManager.Stop("server", DzlConfig.Default(), config);
        stop.Should().Throw<InvalidOperationException>().WithMessage("*no process was stopped*");
        StateFile.ReadRaw(config).Should().ContainKey("server");
    }

    [Fact]
    public void Pending_process_reservation_blocks_a_second_start()
    {
        var config = Config();
        Profiles.EnsureDefault(config);
        StateFile.Reserve(config, "server", "debug", "test", "DayZDiag_x64.exe");
        var result = new LauncherService(config).StartTarget("server", "debug");
        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("unverified ownership");
        StateFile.ReadRaw(config)["server"].Pid.Should().Be(0);
    }

    [Fact]
    public void Failed_process_start_clears_its_prelaunch_reservation()
    {
        var config = Config();
        Profiles.EnsureDefault(config);
        var result = new LauncherService(config).StartTarget("server", "debug");
        result.Ok.Should().BeFalse();
        StateFile.ReadRaw(config).Should().NotContainKey("server");
    }

    [Fact]
    public async Task Malformed_request_does_not_stop_the_listener()
    {
        var config = Config();
        var name = "dzl-test-" + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        var listener = new PipeServer(() => new LauncherService(config), name).RunAsync(cancellation.Token);
        using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(3000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            await writer.WriteLineAsync("{");
            (await reader.ReadLineAsync()).Should().Contain("bad request");
        }
        var response = await Task.Run(() => PipeClient.Send(new IpcRequest(IpcMethods.Status, null), 3000, name));
        response.Should().NotBeNull();
        response!.Ok.Should().BeTrue();
        cancellation.Cancel();
        await listener.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
