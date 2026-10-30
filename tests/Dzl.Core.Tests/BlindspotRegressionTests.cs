using Dzl.Core.App;
using Dzl.Core.Build;
using Dzl.Core.Build.Preflight;
using Dzl.Core.Config;
using Dzl.Core.Economy;
using Dzl.Core.Ipc;
using Dzl.Core.Launch;
using Dzl.Core.Tools;
using FluentAssertions;

public class BlindspotRegressionTests
{
    private static string Tmp() => Directory.CreateTempSubdirectory("dzl-blindspot-").FullName;
    private static (string Config, string Mission) Fixture()
    {
        var root = Tmp();
        var config = Path.Combine(root, "config.json");
        GlobalStore.Save(new GlobalConfig { ProjectsRoot = root }, config);
        var mission = AddMission(config, "a");
        Profiles.SetActive("a", config);
        return (config, mission);
    }
    private static string AddMission(string config, string name)
    {
        var instance = Profiles.InstanceDir(name, config);
        var mission = Path.Combine(instance, "mpmissions", "dayzOffline.chernarusplus");
        Directory.CreateDirectory(Path.Combine(mission, "db"));
        Profiles.Save(DzlConfig.Default() with { ConfigName = Path.Combine(instance, "serverDZ.cfg") }, name, config);
        return mission;
    }

    [Fact]
    public void Bound_dictionary_rejects_other_mission_and_external_changes()
    {
        var (config, a) = Fixture();
        var b = AddMission(config, "b");
        const string original = "<user_lists><usageflags><user name=\"Combo\"><usage name=\"Farm\"/></user></usageflags></user_lists>";
        File.WriteAllText(Path.Combine(a, "cfglimitsdefinitionuser.xml"), original);
        var bFile = Path.Combine(b, "cfglimitsdefinitionuser.xml");
        File.WriteAllText(bFile, original);
        var service = new DictionaryService(config);
        service.BindEditorSession();
        service.LoadGroups().Should().ContainSingle();
        Profiles.SetActive("b", config);
        service.SetGroupMembers(LimitsKind.Usage, "Combo", new[] { "Town" }).ok.Should().BeFalse();
        service.AddName(LimitsKind.Usage, "Town").ok.Should().BeFalse();
        File.ReadAllText(bFile).Should().Be(original);
        service.BindEditorSession();
        service.SetGroupMembers(LimitsKind.Usage, "Combo", new[] { "Town" }).ok.Should().BeTrue();
        service.SetGroupMembers(LimitsKind.Usage, "Combo", new[] { "Village" }).ok.Should().BeTrue();
        File.WriteAllText(bFile, original);
        service.SetGroupMembers(LimitsKind.Usage, "Combo", new[] { "Military" }).ok.Should().BeFalse();
        File.ReadAllText(bFile).Should().Be(original);
    }

    [Fact]
    public void Duplicate_type_rename_is_rejected_before_any_write()
    {
        var (config, mission) = Fixture();
        var file = Path.Combine(mission, "db", "types.xml");
        const string original = "<types><type name=\"A\"><nominal>10</nominal></type><type name=\"B\"><nominal>20</nominal></type></types>";
        File.WriteAllText(file, original);
        var service = new TypesService(config);
        var snapshot = service.LoadSnapshot();
        var entries = snapshot.Rows.Select(r => r.Entry with { Name = "B" }).ToList();
        service.SaveAll(entries, snapshot).Ok.Should().BeFalse();
        File.ReadAllText(file).Should().Be(original);
        service.Backups().Should().BeEmpty();
    }

    [Fact]
    public void Partial_types_save_returns_revisions_that_allow_retry()
    {
        var (config, mission) = Fixture();
        var primary = Path.Combine(mission, "db", "types.xml");
        var otherDir = Directory.CreateDirectory(Path.Combine(mission, "custom")).FullName;
        var other = Path.Combine(otherDir, "other.xml");
        File.WriteAllText(primary, "<types><type name=\"A\"><nominal>1</nominal></type></types>");
        File.WriteAllText(other, "<types><type name=\"B\"><nominal>2</nominal></type></types>");
        File.WriteAllText(Path.Combine(mission, "cfgeconomycore.xml"), "<economycore><ce folder=\"custom\"><file name=\"other.xml\" type=\"types\"/></ce></economycore>");
        var service = new TypesService(config);
        var snapshot = service.LoadSnapshot();
        var entries = snapshot.Rows.Select(r => r.Entry with { Nominal = r.Entry.Nominal + 10 }).ToList();
        var blocked = Path.Combine(otherDir, ".dzl-other-backups");
        File.WriteAllText(blocked, "block backup directory creation");
        service.SaveAll(entries, snapshot, out var retry).Ok.Should().BeFalse();
        File.ReadAllText(primary).Should().Contain("11");
        retry.Revisions[primary].Should().NotBe(snapshot.Revisions[primary]);
        File.Delete(blocked);
        service.SaveAll(entries, retry).Ok.Should().BeTrue();
        service.List().Select(e => e.Nominal).Should().BeEquivalentTo(new[] { 11, 12 });
    }

    [Theory]
    [InlineData(true, null, 0)]
    [InlineData(true, 7, 7)]
    [InlineData(false, null, 42)]
    public void Started_child_never_causes_fallback_even_when_it_has_not_exited(bool started, int? exit, int expected)
    {
        var fallback = 0;
        var result = ProcessElevation.CompleteLaunch(() => (started, exit), () => { fallback++; return 42; });
        fallback.Should().Be(started ? 0 : 1);
        if (started && exit is null) result.Should().BeNull();
        else result.Should().Be(expected);
    }

    [Fact]
    public void Tray_arguments_and_singletons_are_scoped_to_the_selected_config()
    {
        var (config, _) = Fixture();
        TrayLauncher.MonitorArgs(config).Should().Equal("--tray", "--config", IpcContract.CanonicalConfigPath(config));
        using var mutex = new Mutex(false, IpcContract.PipeNameFor(config) + "-tray");
        TrayLauncher.IsTrayRunning(config).Should().BeTrue();
        TrayLauncher.IsTrayRunning(Path.Combine(Tmp(), "other.json")).Should().BeFalse();
    }

    [FileSymlinkFact]
    public void File_aliases_share_process_storage_and_remain_aliases_after_config_save()
    {
        var (config, _) = Fixture();
        var alias = Path.Combine(Tmp(), "alias.json");
        File.CreateSymbolicLink(alias, config);
        var pipe = IpcContract.PipeNameFor(config);
        StateFile.Path(alias).Should().Be(StateFile.Path(config));
        StateFile.Reserve(config, "server", "debug", "test", "fake.exe");
        StateFile.ReadRaw(alias).Should().ContainKey("server");
        GlobalStore.Update(alias, cfg => cfg with { EditorPath = "edited-through-alias" });
        File.ResolveLinkTarget(alias, true)!.FullName.Should().Be(config);
        IpcContract.PipeNameFor(alias).Should().Be(pipe);
        StateFile.Path(alias).Should().Be(StateFile.Path(config));
        GlobalStore.Load(config).EditorPath.Should().Be("edited-through-alias");
        StateFile.Clear(alias, "server");
        StateFile.ReadRaw(config).Should().BeEmpty();
    }

    [Fact]
    public void Directory_junction_alias_uses_the_canonical_storage_directory()
    {
        var (config, _) = Fixture();
        var aliasDirectory = Path.Combine(Tmp(), "alias");
        Dzl.Core.Projects.Junction.Ensure(aliasDirectory, Path.GetDirectoryName(config)!).Ok.Should().BeTrue();
        var alias = Path.Combine(aliasDirectory, "config.json");
        StateFile.Path(alias).Should().Be(StateFile.Path(config));
        StateFile.Reserve(alias, "server", "debug", "test", "fake.exe");
        StateFile.ReadRaw(config).Should().ContainKey("server");
        GlobalStore.Update(alias, cfg => cfg with { EditorPath = "alias-edit" });
        IpcContract.PipeNameFor(alias).Should().Be(IpcContract.PipeNameFor(config));
        GlobalStore.Load(config).EditorPath.Should().Be("alias-edit");
    }

    [Fact]
    public void Binarized_build_never_reuses_a_local_only_receipt()
    {
        var (config, _) = Fixture();
        var project = Tmp();
        File.WriteAllText(Path.Combine(project, "config.cpp"), "class CfgPatches {};");
        var addons = Tmp();
        var pbo = Path.Combine(addons, "Example.pbo");
        PboWriter.Pack(project, pbo, "Example").ok.Should().BeTrue();
        var cache = new Dictionary<string, BuildCache.Entry> { ["Example"] = new("old", pbo, DateTime.UtcNow, BuildCache.OutputDigest(pbo)) };
        BuildCache.Save(Path.GetDirectoryName(config)!, cache);
        var log = new List<string>();
        var result = new BuildService(config).TryCacheSkip("Example", project, "missing-tool", Tmp(), addons,
            true, false, false, null, null, log.Add, Tmp());
        result.skip.Should().BeNull();
        result.stateHash.Should().BeEmpty("no reusable receipt may be written without dependency coverage");
        log.Should().Contain(line => line.Contains("external dependencies"));
    }

    [Fact]
    public async Task Canonical_identity_lookups_do_not_hold_replaceable_config_handles()
    {
        var (config, _) = Fixture();
        var writes = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
                GlobalStore.Update(config, cfg => cfg with { EditorPath = cfg.EditorPath + "x" });
        }));
        await Task.WhenAll(writes);
        GlobalStore.Load(config).EditorPath.Length.Should().Be(100);
    }

    [Fact]
    public void Non_binarized_cache_reuses_valid_output_but_rejects_a_malformed_receipt()
    {
        var (config, _) = Fixture();
        var project = Tmp();
        File.WriteAllText(Path.Combine(project, "asset.txt"), "data");
        var addons = Tmp();
        var pbo = Path.Combine(addons, "Example.pbo");
        PboWriter.Pack(project, pbo, "Example").ok.Should().BeTrue();
        var tools = Tmp();
        var settings = BuildCache.SettingsFingerprint(project, false, false, "missing", null, null, null);
        var state = BuildCache.ComputeStateHash(project, PreflightOptions.DefaultExcludes, settings);
        void SaveReceipt() => BuildCache.Save(Path.GetDirectoryName(config)!, new Dictionary<string, BuildCache.Entry>
        { ["Example"] = new(state, pbo, DateTime.UtcNow, BuildCache.OutputDigest(pbo)) });
        SaveReceipt();
        var service = new BuildService(config);
        service.TryCacheSkip("Example", project, "missing", Tmp(), addons,
            false, false, false, null, null, null, tools).skip.Should().NotBeNull();
        var bytes = File.ReadAllBytes(pbo);
        bytes[^22] ^= 0x40;
        File.WriteAllBytes(pbo, bytes);
        SaveReceipt(); // even a matching content hash cannot establish archive validity
        service.TryCacheSkip("Example", project, "missing", Tmp(), addons,
            false, false, false, null, null, null, tools).skip.Should().BeNull();
    }

    [Fact]
    public void Engine_ignores_locked_obsolete_files_in_previous_staging()
    {
        var src = Tmp();
        File.WriteAllText(Path.Combine(src, "keep.txt"), "current");
        var work = Tmp();
        Directory.CreateDirectory(Path.Combine(work, "Example"));
        var obsolete = Path.Combine(work, "Example", "obsolete.txt");
        File.WriteAllText(obsolete, "stale");
        using var locked = new FileStream(obsolete, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = BuildEngine.Run(Tmp(), src, "Example", "Example", work, Tmp(), false, null, null);
        result.Ok.Should().BeTrue(result.Output);
        PboHeader.Read(result.Pbo)!.Entries.Select(e => e.Name).Should().Contain("keep.txt").And.NotContain("obsolete.txt");
        File.Exists(obsolete).Should().BeTrue("old staging is not a source for this invocation");
    }

    [Fact]
    public void Changing_input_length_during_pack_preserves_previous_output()
    {
        var src = Tmp();
        var input = Path.Combine(src, "a.txt");
        File.WriteAllText(input, "old");
        var pbo = Path.Combine(Tmp(), "out.pbo");
        File.WriteAllText(pbo, "previous-output");
        var result = PboWriter.Pack(src, pbo, "Example", line =>
        { if (line.StartsWith("packing ")) File.AppendAllText(input, "changed-length"); });
        result.ok.Should().BeFalse();
        File.ReadAllText(pbo).Should().Be("previous-output");
        Directory.GetFiles(Path.GetDirectoryName(pbo)!, "*.tmp").Should().BeEmpty();
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("truncate")]
    [InlineData("prefix")]
    public void Archive_verification_rejects_corruption_and_wrong_prefix(string kind)
    {
        var src = Tmp();
        File.WriteAllText(Path.Combine(src, "a.txt"), "payload");
        var pbo = Path.Combine(Tmp(), "out.pbo");
        PboWriter.Pack(src, pbo, "Example").ok.Should().BeTrue();
        PboValidator.Validate(pbo, "Example").ok.Should().BeTrue();
        var bytes = File.ReadAllBytes(pbo);
        if (kind == "payload") { bytes[^22] ^= 0x40; File.WriteAllBytes(pbo, bytes); }
        if (kind == "truncate") File.WriteAllBytes(pbo, bytes[..^1]);
        PboValidator.Validate(pbo, kind == "prefix" ? "Other" : "Example").ok.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extraction_repairs_deleted_or_same_length_corrupt_output(bool delete)
    {
        var src = Tmp();
        File.WriteAllText(Path.Combine(src, "asset.txt"), "healthy");
        var game = Tmp();
        var pbo = Path.Combine(game, "data.pbo");
        PboWriter.Pack(src, pbo, "dz\\test").ok.Should().BeTrue();
        var dest = Tmp();
        var tool = Path.Combine(Tmp(), "fake-bankrev.exe");
        File.WriteAllText(tool, "not executed");
        var runs = 0;
        bool Extract(string archive, string root)
        {
            runs++;
            var header = PboHeader.Read(archive)!;
            using var stream = File.OpenRead(archive);
            foreach (var entry in header.Entries)
            {
                var output = Path.Combine(root, header.Prefix, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                stream.Position = entry.Offset;
                var bytes = new byte[entry.DataSize];
                stream.ReadExactly(bytes);
                File.WriteAllBytes(output, bytes);
            }
            return true;
        }
        GameUnpack.UnpackAllCore(tool, game, dest, false, Extract).Ok.Should().BeTrue();
        GameUnpack.UnpackAllCore(tool, game, dest, false, Extract).Skipped.Should().Be(1);
        var asset = Path.Combine(dest, "dz", "test", "asset.txt");
        if (delete) File.Delete(asset); else File.WriteAllText(asset, "corrupt");
        GameUnpack.UnpackAllCore(tool, game, dest, false, Extract).Extracted.Should().Be(1);
        File.ReadAllText(asset).Should().Be("healthy");
        GameUnpack.UnpackAllCore(tool, game, dest, true, Extract).Extracted.Should().Be(1);
        runs.Should().Be(3);
    }

    [Fact]
    public void Extraction_exit_success_without_outputs_does_not_become_a_valid_receipt()
    {
        var src = Tmp();
        File.WriteAllText(Path.Combine(src, "missing.txt"), "data");
        var game = Tmp();
        PboWriter.Pack(src, Path.Combine(game, "data.pbo"), "dz\\test").ok.Should().BeTrue();
        var tool = Path.Combine(Tmp(), "fake-bankrev.exe");
        File.WriteAllText(tool, "not executed");
        var result = GameUnpack.UnpackAllCore(tool, game, Tmp(), false, (_, _) => true);
        result.Ok.Should().BeFalse();
        result.Failed.Should().Be(1);
    }
}

/// <summary>Explicitly report hosts without file-symlink privileges; never count that scenario as passed.</summary>
public sealed class FileSymlinkFactAttribute : FactAttribute
{
    public FileSymlinkFactAttribute()
    {
        var root = Directory.CreateTempSubdirectory("dzl-link-probe-").FullName;
        var target = Path.Combine(root, "target");
        var alias = Path.Combine(root, "alias");
        try
        {
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(alias, target);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
        { Skip = "Host lacks Windows file-symlink privilege; this native alias scenario was not run."; }
        finally
        {
            File.Delete(alias);
            File.Delete(target);
            Directory.Delete(root);
        }
    }
}
