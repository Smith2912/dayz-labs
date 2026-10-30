using Dzl.Core.Launch;
using FluentAssertions;

public class StateFileTests
{
    private static string Cfg() => Path.Combine(Directory.CreateTempSubdirectory().FullName, "config.json");

    [Fact]
    public void Path_is_next_to_config_and_distinct_per_config()
    {
        var first = StateFile.Path(Path.Combine("x", "config.json")).Replace('\\', '/');
        first.Should().Contain("/.dzl-procs.").And.EndWith(".json");
        first.Should().NotBe(StateFile.Path(Path.Combine("x", "other.json")).Replace('\\', '/'));
    }

    [Fact]
    public void Write_raw_read_clear_round_trip()
    {
        var cfg = Cfg();
        StateFile.Write(cfg, "server", 4242, "debug", "cli", "DayZDiag_x64.exe");
        StateFile.ReadRaw(cfg)["server"].Pid.Should().Be(4242);
        StateFile.ReadRaw(cfg)["server"].Source.Should().Be("cli");
        StateFile.Clear(cfg, "server");
        StateFile.ReadRaw(cfg).Should().NotContainKey("server");
    }

    [Fact]
    public void Reconcile_preserves_legacy_records_when_ownership_is_unknown()
    {
        var cfg = Cfg();
        StateFile.Write(cfg, "server", 999999, "debug", "cli", "DayZDiag_x64.exe");
        StateFile.ReadLive(cfg, _ => null).Should().ContainKey("server");
        StateFile.Write(cfg, "server", 4242, "debug", "cli", "DayZDiag_x64.exe");
        StateFile.ReadLive(cfg, _ => "notepad.exe").Should().ContainKey("server");
        StateFile.Write(cfg, "server", 4242, "debug", "cli", "DayZDiag_x64.exe");
        StateFile.ReadLive(cfg, _ => "DayZDiag_x64.exe").Should().ContainKey("server"); // alive, matching image
    }

    [Fact]
    public void Missing_file_reads_empty_but_corrupt_file_is_preserved()
    {
        var cfg = Cfg();
        StateFile.ReadRaw(cfg).Should().BeEmpty();
        File.WriteAllText(StateFile.Path(cfg), "{ broken");
        Action read = () => StateFile.ReadRaw(cfg);
        read.Should().Throw<InvalidDataException>();
        File.ReadAllText(StateFile.Path(cfg)).Should().Be("{ broken");
    }

    [Fact]
    public void Locked_file_fails_closed_instead_of_hiding_ownership()
    {
        // A sharing violation must not hide ownership and permit a second spawn.
        var cfg = Cfg();
        StateFile.Write(cfg, "server", 4242, "debug", "cli", "DayZDiag_x64.exe");
        using var lockStream = new FileStream(StateFile.Path(cfg), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Action read = () => StateFile.ReadRaw(cfg);
        read.Should().Throw<InvalidDataException>();
    }
}
