using Dzl.Core.Procs;

namespace Dzl.Core.Tools;

public static class CfgConvert
{
    public static List<string> UnbinarizeArgs(string binPath, string outCpp) =>
        new() { "-txt", "-dst", outCpp, binPath };

    public static (bool ok, string output) Unbinarize(string exePath, string binPath, string outCpp) =>
        Convert(exePath, binPath, outCpp, binary: false);

    public static List<string> ToBinArgs(string cppPath, string outBin) =>
        new() { "-bin", "-dst", outBin, cppPath };

    /// <summary>Convert a <c>config.cpp</c> to binary form — used both for real conversion and as a
    /// syntax gate. Requires successful exit and actual binary output; the exit code alone is
    /// insufficient. Runs with cwd at the config's folder so relative includes resolve.</summary>
    public static (bool ok, string output) ToBin(string exePath, string cppPath, string outBin) =>
        Convert(exePath, cppPath, outBin, binary: true);

    private static (bool ok, string output) Convert(string exePath, string input, string output, bool binary)
    {
        string? temp = null;
        try
        {
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            temp = Path.Combine(Path.GetDirectoryName(output)!, ".dzl-convert-" + Guid.NewGuid().ToString("N") + (binary ? ".bin" : ".cpp"));
            using var paths = new NativePathScope();
            var source = paths.FilePath(input);
            var dest = paths.FilePath(temp);
            var args = binary ? ToBinArgs(source, dest) : UnbinarizeArgs(source, dest);
            var r = ProcRunner.Run(exePath, args, new RunOpts(WorkingDir: paths.DirectoryPath(Path.GetDirectoryName(input)!)));
            var valid = File.Exists(temp) && new FileInfo(temp).Length > 0;
            if (valid && binary)
            {
                using var stream = File.OpenRead(temp);
                var magic = new byte[4];
                valid = stream.Read(magic) == 4 && magic.SequenceEqual(new byte[] { 0, (byte)'r', (byte)'a', (byte)'P' });
            }
            if (!r.Ok || !valid) return (false, r.AllOutput + $"\nCfgConvert did not produce valid {(binary ? "binary" : "text")} output (exit {r.Code}).");
            File.Move(temp, output, overwrite: true);
            return (true, r.AllOutput);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
