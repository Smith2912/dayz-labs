using Dzl.Core.Procs;

namespace Dzl.Core.Tools;

/// <summary>Thin wrapper around DayZ Tools' <c>FileBank.exe</c>. Packs a prepared folder as-is (no
/// binarization) into <c>&lt;sourceLeaf&gt;.pbo</c> under <paramref name="outDir"/>, stamping the PBO prefix.
/// Never throws.</summary>
public static class FileBank
{
    public static List<string> PackArgs(string sourceDir, string outDir, string prefix) =>
        new() { "-property", $"prefix={prefix}", "-dst", outDir, sourceDir };

    public static (bool ok, string output) Pack(string exePath, string sourceDir, string outDir, string prefix,
        Action<string>? onLine = null)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            using var paths = new NativePathScope();
            // Preserve the source leaf: FileBank derives the output PBO name from it.
            var source = paths.FilePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDir)));
            var r = ProcRunner.Run(exePath, PackArgs(source, paths.DirectoryPath(outDir), prefix),
                new RunOpts(TimeoutMs: 0, OnLine: onLine));
            var output = Path.Combine(outDir, Path.GetFileName(source) + ".pbo");
            return (r.Ok && Dzl.Core.Build.PboValidator.HasValidChecksum(output), r.AllOutput);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
