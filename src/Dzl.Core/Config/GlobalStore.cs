using System.Text.Json;
using Dzl.Core.Launch;

namespace Dzl.Core.Config;

/// <summary>Loads/saves the machine-global <see cref="GlobalConfig"/> to <c>config.json</c>.
/// Snake_case JSON, unknown keys tolerated (so a legacy full-config file still yields its global
/// slice on read; migration rewrites it to the global-only shape).</summary>
public static class GlobalStore
{
    public static GlobalConfig Load(string configPath)
    {
        if (!File.Exists(configPath)) return new GlobalConfig();
        try { return JsonSerializer.Deserialize<GlobalConfig>(File.ReadAllText(configPath), ConfigStore.Json)
                ?? throw new JsonException("Configuration must be an object."); }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid configuration at '{configPath}'; the original was preserved.", ex); }
    }

    public static void Save(GlobalConfig cfg, string configPath)
    {
        using var guard = StateFile.AcquireLock(configPath);
        SaveLocked(cfg, configPath);
    }

    /// <summary>Read, change and save one global config under the shared configuration lock.</summary>
    public static GlobalConfig Update(string configPath, Func<GlobalConfig, GlobalConfig> update)
    {
        using var guard = StateFile.AcquireLock(configPath);
        var cfg = update(Load(configPath));
        SaveLocked(cfg, configPath);
        return cfg;
    }

    private static void SaveLocked(GlobalConfig cfg, string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        // Refuse to overwrite a damaged document. Retain a copy of the last valid
        // version before atomically publishing the complete replacement.
        if (File.Exists(configPath))
        {
            _ = Load(configPath);
            File.Copy(configPath, configPath + ".bak", overwrite: true);
        }
        var temp = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(cfg, ConfigStore.Json));
            File.Move(temp, configPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
