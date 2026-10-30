using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dzl.Core.Tools;

/// <summary>Launches a child process at the NORMAL (non-elevated) user level even when the current
/// process is elevated — so things it mounts (the P: work drive) land in the user session the
/// game/Explorer see.</summary>
/// <remarks>Uses the current interactive shell's non-elevated token + CreateProcessWithTokenW.
/// If normal-user launch cannot be established, it fails closed instead of launching elevated.</remarks>
public static class ProcessElevation
{
    /// <summary>Run exe+args, returning the exit code (null if it couldn't start / timed out).
    /// De-elevates to the user session when <paramref name="deElevateIfAdmin"/> and we're admin.</summary>
    public static int? Run(string exePath, IReadOnlyList<string> args, string workingDir, int timeoutMs,
                           bool deElevateIfAdmin, bool showWindow = false, Action<string>? onDiagnostic = null)
    {
        if (deElevateIfAdmin && OperatingSystem.IsWindows() && Dzl.Core.Env.EnvDetect.IsElevated())
        {
            var attempt = TryRunDeElevated(exePath, args, workingDir, timeoutMs, showWindow, onDiagnostic);
            return CompleteLaunch(() => attempt,
                () => null); // Never silently run elevated when normal-user execution was requested.
        }
        return RunNormal(exePath, args, workingDir, timeoutMs, showWindow);
    }

    internal static int? CompleteLaunch(Func<(bool Started, int? ExitCode)> preferred, Func<int?> fallback)
    {
        var result = preferred();
        return result.Started ? result.ExitCode : fallback();
    }

    private static int? RunNormal(string exePath, IReadOnlyList<string> args, string workingDir, int timeoutMs, bool showWindow)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            { UseShellExecute = false, CreateNoWindow = !showWindow, WorkingDirectory = workingDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            return p.WaitForExit(timeoutMs) ? p.ExitCode : (int?)null;
        }
        catch { return null; }
    }

    // Spawn the child with the current interactive shell's normal user token.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (bool Started, int? ExitCode) TryRunDeElevated(string exePath, IReadOnlyList<string> args, string workingDir, int timeoutMs, bool showWindow, Action<string>? diagnostic)
    {
        (bool, int?) Failed(string operation)
        {
            diagnostic?.Invoke($"{operation} failed (Win32 {Marshal.GetLastWin32Error()})");
            return (false, null);
        }
        bool started = false;
        IntPtr hProc = IntPtr.Zero, hToken = IntPtr.Zero, hPrimary = IntPtr.Zero;
        try
        {
            var shell = GetShellWindow();
            if (shell == IntPtr.Zero) return Failed("Interactive shell unavailable");
            _ = GetWindowThreadProcessId(shell, out var pid);
            if (!ProcessIdToSessionId(pid, out var shellSession) || shellSession != (uint)Process.GetCurrentProcess().SessionId)
                return Failed("Shell session mismatch");
            hProc = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (hProc == IntPtr.Zero) return Failed("Open shell process");
            if (!OpenProcessToken(hProc, TOKEN_QUERY | TOKEN_DUPLICATE, out hToken)) return Failed("OpenProcessToken");
            var buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenElevation, buf, sizeof(int), out _))
                    return Failed("Shell token elevation query");
                if (Marshal.ReadInt32(buf) != 0) return Failed("Interactive shell is elevated");
            }
            finally { Marshal.FreeHGlobal(buf); }
            // Linked UAC tokens can be identification-only on real hosts (Win32 1346).
            // Duplicate the actual shell primary token without enabling any privileges.
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(hToken, 0x02000000 /* MAXIMUM_ALLOWED by the shell token's existing DACL */, ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out hPrimary))
                return Failed("DuplicateTokenEx");

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmdLine = BuildCommandLine(exePath, args);   // mutable buffer
            if (!CreateProcessWithTokenW(hPrimary, LOGON_WITH_PROFILE, exePath, cmdLine,
                    showWindow ? 0u : CREATE_NO_WINDOW, IntPtr.Zero, workingDir, ref si, out var pi))
                return Failed("CreateProcessWithTokenW");

            started = true;
            try
            {
                uint wait = WaitForSingleObject(pi.hProcess, (uint)timeoutMs);
                if (wait != 0 /*WAIT_OBJECT_0*/) return (true, null);
                return (true, GetExitCodeProcess(pi.hProcess, out uint exit) ? (int)exit : (int?)null);
            }
            finally
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            }
        }
        catch (Exception ex) { diagnostic?.Invoke(ex.Message); return (started, null); }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hPrimary != IntPtr.Zero) CloseHandle(hPrimary);
            if (hProc != IntPtr.Zero) CloseHandle(hProc);
        }
    }

    // CreateProcessWithTokenW wants a single command-line string; quote the exe + args.
    // Public + pure so the quoting rules are unit-testable (the rest of this class needs a real
    // elevated token).
    public static string BuildCommandLine(string exePath, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('"').Append(exePath).Append('"');
        foreach (var a in args)
        {
            sb.Append(' ');
            AppendArgument(sb, a);
        }
        return sb.ToString();
    }

    /// <summary>MSVCRT argv quoting: backslashes are literal except when they precede a quote (or
    /// the closing quote), where each must be doubled. The naive Replace("\"","\\\"") variant broke
    /// args ending in a backslash — <c>"C:\DayZ Projects\"</c> parses as an escaped quote and
    /// swallows the next argument.</summary>
    private static void AppendArgument(System.Text.StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && !arg.Any(c => c is ' ' or '\t' or '"'))
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var i = 0;
        while (i < arg.Length)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);   // before the closing quote: double them
            }
            else if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                i++;
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
                i++;
            }
        }
        sb.Append('"');
    }

    private const uint TOKEN_QUERY = 0x0008, TOKEN_DUPLICATE = 0x0002;
    private const uint LOGON_WITH_PROFILE = 0x00000001, CREATE_NO_WINDOW = 0x08000000;

    private enum TOKEN_INFORMATION_CLASS { TokenElevation = 20 }
    private enum SECURITY_IMPERSONATION_LEVEL { SecurityImpersonation = 2 }
    private enum TOKEN_TYPE { TokenPrimary = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS cls, IntPtr info, int len, out int retLen);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, ref SECURITY_ATTRIBUTES attrs, SECURITY_IMPERSONATION_LEVEL level, TOKEN_TYPE type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? appName, string cmdLine, uint creationFlags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);
}
