using Dzl.Core.Projects;

namespace Dzl.Core.Tools;

/// <summary>Short-lived ASCII directory aliases for legacy DayZ utilities that use ANSI argv.
/// Junctions preserve relative includes and write directly to the intended directory; no drive is mounted.</summary>
internal sealed class NativePathScope : IDisposable
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private string? _root;

    public string DirectoryPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || path.All(c => c <= 127)) return path;
        if (path.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '!', '\r', '\n', '"' }) >= 0)
            throw new IOException("Native tool alias paths cannot contain shell metacharacters; use a private ASCII staging folder.");
        if (_aliases.TryGetValue(path, out var existing)) return existing;
        var parent = Path.GetFullPath(Environment.GetEnvironmentVariable("DZL_NATIVE_TEMP") ?? Path.GetTempPath());
        if (parent.Any(c => c > 127) || parent.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '!', '\r', '\n', '"' }) >= 0)
            throw new IOException("Native DayZ tools need a plain ASCII temporary path; set DZL_NATIVE_TEMP to a private ASCII folder.");
        _root ??= Path.Combine(parent, "dzl-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var link = Path.Combine(_root, "d" + _aliases.Count);
        var result = Junction.Ensure(link, path);
        if (!result.Ok) throw new IOException($"Cannot create native tool path alias: {result.Detail}");
        _aliases[path] = link;
        return link;
    }

    public string FilePath(string path)
    {
        path = Path.GetFullPath(path);
        var name = Path.GetFileName(path);
        if (OperatingSystem.IsWindows() && name.Any(c => c > 127))
            throw new IOException("This native DayZ utility requires an ASCII filename; rename the file before retrying.");
        return Path.Combine(DirectoryPath(Path.GetDirectoryName(path)!), name);
    }

    public void Dispose()
    {
        foreach (var link in _aliases.Values) Junction.Remove(link); // remove only the owned link, never its target
        if (_root is not null) try { System.IO.Directory.Delete(_root); } catch { /* retain failed cleanup for inspection */ }
    }
}
