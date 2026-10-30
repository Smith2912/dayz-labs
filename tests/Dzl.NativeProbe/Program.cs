using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Dzl.Core.Tools;

// Opt-in harmless native token probe. No game processes, drive changes, keys, or network operations.
if (args.Length < 2) return 2;
var mode = args[0];
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var identity = WindowsIdentity.GetCurrent();
var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
GetTokenInformation(identity.Token, 18, out var elevationType, sizeof(int), out _);
if (mode == "--child")
{
    var output = Path.Combine(root, "child-" + Environment.ProcessId + ".json");
    File.WriteAllText(output, JsonSerializer.Serialize(new { Pid = Environment.ProcessId, Elevated = elevated, ElevationType = elevationType, Args = args.Skip(2).ToArray() }));
    Thread.Sleep(400);
    return 17;
}
if (mode != "--parent") return 2;
if (Directory.EnumerateFileSystemEntries(root).Any())
{ Console.Error.WriteLine("Use a fresh, empty receipt directory for the parent probe."); return 2; }
var host = Environment.ProcessPath!;
var assembly = typeof(Program).Assembly.Location;
var childArgs = host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
    ? new List<string> { assembly, "--child", root, "space argument", @"trailing\" }
    : new List<string> { "--child", root, "space argument", @"trailing\" };
var diagnostics = new List<string>();
var systemExit = ProcessElevation.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/c", "exit", "17" },
    root, 5000, deElevateIfAdmin: true, onDiagnostic: diagnostics.Add);
var zeroWait = ProcessElevation.Run(host, childArgs, root, 0, deElevateIfAdmin: true, onDiagnostic: diagnostics.Add);
var watch = Stopwatch.StartNew();
while (Directory.GetFiles(root, "child-*.json").Length < 1 && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
Thread.Sleep(800); // a duplicate fallback creation must also have time to write its receipt
var first = Directory.GetFiles(root, "child-*.json").Length;
var waited = ProcessElevation.Run(host, childArgs, root, 5000, deElevateIfAdmin: true, onDiagnostic: diagnostics.Add);
Thread.Sleep(200);
var children = Directory.GetFiles(root, "child-*.json").Select(file => JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone()).ToArray();
var passed = elevated && elevationType == 2 && systemExit == 17 && zeroWait is null && first == 1 && waited == 17 && children.Length == 2 &&
    children.All(child => !child.GetProperty("Elevated").GetBoolean() && child.GetProperty("ElevationType").GetInt32() == 3 &&
        child.GetProperty("Args")[0].GetString() == "space argument" && child.GetProperty("Args")[1].GetString() == @"trailing\");
File.WriteAllText(Path.Combine(root, "parent-result.json"), JsonSerializer.Serialize(new
{ Passed = passed, ParentElevated = elevated, ParentElevationType = elevationType, SystemExit = systemExit, ZeroWaitExit = zeroWait, ZeroWaitChildren = first, WaitedExit = waited, Children = children, Diagnostics = diagnostics }, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
