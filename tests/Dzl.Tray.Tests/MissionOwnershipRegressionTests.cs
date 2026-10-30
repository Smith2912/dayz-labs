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
}
