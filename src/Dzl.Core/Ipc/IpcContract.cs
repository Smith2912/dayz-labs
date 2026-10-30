using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Dzl.Core.Json;

namespace Dzl.Core.Ipc;

public sealed record IpcRequest(string Method, Dictionary<string, string>? Args);
public sealed record IpcResponse(bool Ok, string? Error, string? Json);

public static class IpcContract
{
    public const string PipeName = "dzl-ipc-v1";
    public static string PipeNameFor(string configPath)
    {
        var path = CanonicalConfigPath(configPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var key = Environment.UserName + "\n" + path;
        return "dzl-ipc-v2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    /// <summary>Resolve file links and parent junctions without opening the replaceable config file.
    /// Even a delete-shared file handle can briefly hold a Windows replacement target delete-pending.</summary>
    public static string CanonicalConfigPath(string configPath)
    {
        var path = Path.GetFullPath(configPath);
        if (!OperatingSystem.IsWindows()) return path;
        if (new FileInfo(path).LinkTarget is not null)
            path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? throw new IOException($"Cannot resolve config link '{path}'.");
        var existing = Path.GetDirectoryName(path)!;
        while (!Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new IOException($"Cannot resolve config path '{path}'.");
        using var handle = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Cannot resolve config path '{existing}'.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity) throw new IOException($"Cannot resolve config path '{existing}'.");
        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return Path.GetFullPath(Path.Combine(resolved, Path.GetRelativePath(existing, path)));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    // MUST stay WriteIndented=false (DzlJson.Snake): the pipe protocol is line-framed —
    // one JSON document per line — so an indented payload would split across reads.
    public static readonly JsonSerializerOptions Json = DzlJson.Snake;
}
