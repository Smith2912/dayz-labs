namespace Dzl.Core.Config;

/// <summary>Per-server settings — the unit of work in dzl. Each server instance owns its mission/map,
/// port, serverDZ.cfg, profiles dirs, mod loadout, launch params and mode.</summary>
/// <remarks>Persisted as <c>instances/&lt;name&gt;.json</c>; composed with the single
/// <see cref="GlobalConfig"/> at runtime into a <see cref="DzlConfig"/>. Defaults mirror
/// <see cref="DzlConfig"/> (a round-trip test guards drift).</remarks>
public sealed record InstanceConfig
{
    public string ProfilesPath { get; init; } = @"E:\Steam\steamapps\common\DayZ\profiles";
    public string ClientProfilesPath { get; init; } = @"E:\Steam\steamapps\common\DayZ\profiles_client";
    public int Port { get; init; } = 2302;
    public string Mission { get; init; } = "./mpmissions/dayzOffline.chernarusplus";
    public string PlayerName { get; init; } = "DevMacie";
    public string ConfigName { get; init; } = "serverDZ.cfg";
    public string ConnectIp { get; init; } = "127.0.0.1";
    public List<ModEntry> Mods { get; init; } = new();
    public string Mode { get; init; } = "debug";
    /// <summary>Name of the last-applied mod preset (loadout) — "" = none. Drives the preset
    /// combo selection in the UI; a dangling name (preset deleted) is harmless. snake_case: mod_preset.</summary>
    public string ModPreset { get; init; } = "";

    /// <summary>Client-only offline sandbox: the instance has no server — the dashboard hides the
    /// server card and the client always starts without -connect/-port (mods + mission load, no
    /// auto-join). snake_case: offline_mode.</summary>
    public bool OfflineMode { get; init; }

    public List<string> ServerParamsDebug { get; init; } = new() { "-filePatching", "-dologs", "-adminLog", "-freezecheck", "-limitFPS=120" };
    public List<string> ServerParamsNormal { get; init; } = new() { "-dologs", "-adminLog", "-freezecheck" };
    public List<string> ClientParamsDebug { get; init; } = new() { "-window", "-nosplash", "-filePatching", "-doLogs", "-scriptDebug=true" };
    public List<string> ClientParamsNormal { get; init; } = new() { "-window", "-nosplash" };
}
