using System.IO;
using Dzl.Core.Config;
using Dzl.Core.App;
using Dzl.Tray.Controls;
using Dzl.Tray.ViewModels;
using Dzl.Tray;
using FluentAssertions;

public class MissionOwnershipRegressionTests
{
    private const string A = "<variables><!-- A --><var name=\"Review\" type=\"0\" value=\"1\"/></variables>";
    private const string B = "<variables><!-- B --><var name=\"Review\" type=\"0\" value=\"2\"/></variables>";

    [Fact]
    public void Setup_preserves_unrelated_global_settings()
    {
        var existing = new GlobalConfig { EditorPath = "editor", SigningKey = "key", SteamLogin = "account",
            AutomountWorkDrive = true, EnableAutomationServer = true, ActiveInstance = "existing" };
        var merged = SetupWizardWindow.MergeWizardSettings(existing, "game", "tools", "server", "root", new List<string> { "mods" });
        merged.EditorPath.Should().Be("editor");
        merged.SigningKey.Should().Be("key");
        merged.SteamLogin.Should().Be("account");
        merged.AutomountWorkDrive.Should().BeTrue();
        merged.EnableAutomationServer.Should().BeTrue();
        merged.ActiveInstance.Should().Be("existing");
        merged.DayzPath.Should().Be("game");
        merged.ScanRoots.Should().Equal("mods");
    }

    [Fact]
    public void Undo_and_edit_are_blocked_after_an_external_instance_switch()
    {
        var config = CeScaffold.Mission(("db/globals.xml", A));
        var other = Profiles.InstanceDir("other", config);
        var otherDb = Path.Combine(other, "mpmissions", "dayzOffline.chernarusplus", "db");
        Directory.CreateDirectory(otherDb);
        var otherFile = Path.Combine(otherDb, "globals.xml");
        File.WriteAllText(otherFile, B);
        Profiles.Save(DzlConfig.Default() with { ConfigName = Path.Combine(other, "serverDZ.cfg") }, "other", config);
        var vm = new GlobalsVm(config, _ => true);
        vm.EnsureLoaded();
        vm.SelectedRow = vm.Rows.Single();
        vm.DetailValue = "3";
        vm.SaveDetail();
        vm.CanUndo.Should().BeTrue();
        var boundService = new GlobalsService(config);
        boundService.BindEditorSession(boundService.GlobalsPath());
        Profiles.SetActive("other", config);
        boundService.WriteRaw(A).ok.Should().BeFalse();
        vm.UndoCommand.Execute(null);
        File.ReadAllText(otherFile).Should().Be(B);
        vm.CanUndo.Should().BeTrue();
        vm.DetailValue = "4";
        vm.SaveDetail();
        File.ReadAllText(otherFile).Should().Be(B);
        vm.Status.Should().Contain("mission changed");
        Profiles.SetActive("test", config);
        vm.UndoCommand.Execute(null);
        vm.Status.Should().Be("↶ undo");
    }

    [WpfFact]
    public void Stale_types_draft_cannot_be_saved_to_the_new_mission()
    {
        var config = CeScaffold.Mission(("db/types.xml", "<types><type name=\"Apple\"/></types>"));
        using var vm = new TypesEditorVm(config);
        vm.LoadTypes();
        vm.AddType("NewItem");
        var other = Profiles.InstanceDir("other", config);
        var db = Path.Combine(other, "mpmissions", "dayzOffline.chernarusplus", "db");
        Directory.CreateDirectory(db);
        var file = Path.Combine(db, "types.xml");
        const string original = "<types><type name=\"Other\"/></types>";
        File.WriteAllText(file, original);
        Profiles.Save(DzlConfig.Default() with { ConfigName = Path.Combine(other, "serverDZ.cfg") }, "other", config);
        Profiles.SetActive("other", config);
        vm.SaveTypes();
        vm.HasUnsavedChanges.Should().BeTrue();
        File.ReadAllText(file).Should().Be(original);
        vm.TypesStatus.Should().Contain("mission changed");
    }

    [Fact]
    public void Bound_editor_refuses_to_overwrite_external_changes()
    {
        var config = CeScaffold.Mission(("db/globals.xml", A));
        var service = new GlobalsService(config);
        var path = service.GlobalsPath()!;
        service.BindEditorSession(path);
        File.WriteAllText(path, B);
        var result = service.WriteRaw(A);
        result.ok.Should().BeFalse();
        result.msg.Should().Contain("changed outside");
        File.ReadAllText(path).Should().Be(B);
    }

    [WpfFact]
    public void Restore_does_not_discard_a_dirty_draft_or_undo_history()
    {
        var config = CeScaffold.Mission(("db/types.xml", "<types><type name=\"Apple\"/></types>"));
        var service = new TypesService(config);
        var path = service.TypesFile()!;
        var backup = Dzl.Core.Economy.TypesBackup.Snapshot(path);
        using var vm = new TypesEditorVm(config);
        vm.LoadTypes();
        vm.AddType("Unsaved");
        var original = File.ReadAllText(path);
        vm.RestoreTypes(backup);
        vm.HasUnsavedChanges.Should().BeTrue();
        vm.CanUndoTypes.Should().BeTrue();
        vm.Types.Should().Contain(row => row.Name == "Unsaved");
        File.ReadAllText(path).Should().Be(original);
        vm.TypesStatus.Should().Contain("Save or explicitly discard");
        vm.LoadTypes(); // explicit discard
        vm.RestoreTypes(backup);
        vm.HasUnsavedChanges.Should().BeFalse();
        vm.Types.Should().NotContain(row => row.Name == "Unsaved");
    }

    [Fact]
    public void Dictionary_manager_keeps_stale_combo_edits_out_of_new_mission()
    {
        const string limits = "<user_lists><usageflags><user name=\"Combo\"><usage name=\"Farm\"/></user></usageflags></user_lists>";
        var config = CeScaffold.Mission(("cfglimitsdefinitionuser.xml", limits));
        var other = Profiles.InstanceDir("other", config);
        var mission = Path.Combine(other, "mpmissions", "dayzOffline.chernarusplus");
        Directory.CreateDirectory(mission);
        var file = Path.Combine(mission, "cfglimitsdefinitionuser.xml");
        File.WriteAllText(file, limits);
        Profiles.Save(DzlConfig.Default() with { ConfigName = Path.Combine(other, "serverDZ.cfg") }, "other", config);
        var vm = new DictionaryManagerVm(config, () => { }, (_, _) => 0, _ => true);
        vm.Reload();
        vm.SelectedCombo!.Members.Add("Town");
        Profiles.SetActive("other", config);
        vm.SaveSelectedComboMembers();
        vm.Status.Should().Contain("mission or file changed");
        File.ReadAllText(file).Should().Be(limits);
        vm.Reload();
        vm.SelectedCombo!.Members.Add("Town");
        vm.SaveSelectedComboMembers();
        File.ReadAllText(file).Should().Contain("Town");
    }

    [WpfFact]
    public void Duplicate_type_rename_retains_the_dirty_draft_and_history()
    {
        var config = CeScaffold.Mission(("db/types.xml", "<types><type name=\"A\"/><type name=\"B\"/></types>"));
        using var vm = new TypesEditorVm(config);
        vm.LoadTypes();
        vm.PushDetailEditUndo();
        vm.Types.First().Name = "B";
        vm.SaveTypes();
        vm.HasUnsavedChanges.Should().BeTrue();
        vm.CanUndoTypes.Should().BeTrue();
        vm.Types.Count.Should().Be(2);
        vm.TypesStatus.Should().Contain("duplicate type names");
        new TypesService(config).List().Select(e => e.Name).Should().Equal("A", "B");
    }

    [WpfFact]
    public void Partial_save_retry_keeps_and_completes_the_vm_draft()
    {
        var config = CeScaffold.Mission(
            ("db/types.xml", "<types><type name=\"A\"><nominal>1</nominal></type></types>"),
            ("custom/other.xml", "<types><type name=\"B\"><nominal>2</nominal></type></types>"),
            ("cfgeconomycore.xml", "<economycore><ce folder=\"custom\"><file name=\"other.xml\" type=\"types\"/></ce></economycore>"));
        using var vm = new TypesEditorVm(config);
        vm.LoadTypes();
        vm.BatchApply(vm.Types.ToList(), "nominal", 25, false);
        var other = vm.Types.Single(row => row.Name == "B").SourceFile;
        var blocker = Path.Combine(Path.GetDirectoryName(other)!, ".dzl-other-backups");
        File.WriteAllText(blocker, "blocked");
        vm.SaveTypes();
        vm.HasUnsavedChanges.Should().BeTrue();
        vm.TypesStatus.Should().Contain("partial save");
        File.Delete(blocker);
        vm.SaveTypes();
        vm.HasUnsavedChanges.Should().BeFalse();
        new TypesService(config).List().Should().OnlyContain(row => row.Nominal == 25);
    }
}
