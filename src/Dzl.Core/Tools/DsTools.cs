using Dzl.Core.Procs;

namespace Dzl.Core.Tools;

public sealed record KeyResult(bool Ok, string PrivateKey, string PublicKey, string Output);

/// <summary>Thin, never-throwing wrappers around DayZ Tools' DsUtils signing binaries. Key creation +
/// signing are CLI-wrappable; the GUI Publisher is launch-only (see <see cref="ToolLauncher"/>).</summary>
public static class DsTools
{
    /// <summary>Run the native signature checker against isolated addon/key directories.</summary>
    public static (bool ok, string output) CheckSignatures(string exePath, string addonsDir, string keysDir)
    {
        try
        {
            var pbos = Directory.GetFiles(addonsDir, "*.pbo", SearchOption.AllDirectories);
            if (pbos.Length == 0 || pbos.Any(pbo => !Dzl.Core.Build.PboValidator.HasValidChecksum(pbo)))
                return (false, "PBO payload/container checksum verification failed, or no PBOs were found.");
            if (pbos.Any(pbo => Directory.GetFiles(Path.GetDirectoryName(pbo)!, Path.GetFileName(pbo) + ".*.bisign").Length == 0))
                return (false, "Missing PBO signature.");
            var keys = Directory.GetFiles(keysDir, "*.bikey", SearchOption.AllDirectories);
            foreach (var pbo in pbos)
                foreach (var signature in Directory.GetFiles(Path.GetDirectoryName(pbo)!, Path.GetFileName(pbo) + ".*.bisign"))
                    if (!Dzl.Core.Build.BiSignature.VerifyPayload(pbo, signature, keys))
                        return (false, "BI signature does not match the current PBO payload or trusted public key: " + Path.GetFileName(pbo));
            var signatures = Directory.GetFiles(addonsDir, "*.bisign", SearchOption.AllDirectories).Length;
            using var paths = new NativePathScope();
            var r = ProcRunner.Run(exePath, new[] { paths.DirectoryPath(addonsDir), paths.DirectoryPath(keysDir) },
                new RunOpts(WorkingDir: paths.DirectoryPath(addonsDir), TimeoutMs: 120_000));
            var verified = System.Text.RegularExpressions.Regex.Matches(r.AllOutput, @"(?m)^Signature .* is OK\r?$").Count;
            return (r.Ok && verified == signatures && verified > 0, r.AllOutput);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Create a signing key pair: <c>DSCreateKey &lt;name&gt;</c> writes <c>&lt;name&gt;.biprivatekey</c>
    /// + <c>&lt;name&gt;.bikey</c> into its working directory. We run it with the working dir set to
    /// <paramref name="keysDir"/> so the pair lands there. Never throws.</summary>
    public static KeyResult CreateKey(string exePath, string keysDir, string name)
    {
        var priv = Path.Combine(keysDir, name + ".biprivatekey");
        var pub = Path.Combine(keysDir, name + ".bikey");
        try
        {
            Directory.CreateDirectory(keysDir);
            if (File.Exists(priv) || File.Exists(pub)) return new KeyResult(false, priv, pub, "Key files already exist; refusing to overwrite them.");
            if (Path.GetFileName(name) != name || name.Any(c => c > 127))
                return new KeyResult(false, priv, pub, "Signing key names must be ASCII filenames.");
            using var paths = new NativePathScope();
            var r = ProcRunner.Run(exePath, new[] { name }, new RunOpts(WorkingDir: paths.DirectoryPath(keysDir)));
            // DSCreateKey can return non-zero even on success on some setups; trust the produced file.
            return new KeyResult(File.Exists(priv) && File.Exists(pub), priv, pub, r.AllOutput);
        }
        catch (Exception ex)
        {
            return new KeyResult(false, priv, pub, ex.Message);
        }
    }

    public static List<string> SignArgs(string privateKey, string pbo) => new() { privateKey, pbo };

    /// <summary>Sign a PBO: <c>DSSignFile &lt;key.biprivatekey&gt; &lt;file.pbo&gt;</c> writes
    /// <c>&lt;file.pbo&gt;.&lt;key&gt;.bisign</c> next to it. Run with the working dir at the PBO's folder so
    /// the signature lands there. Never throws.</summary>
    public static (bool ok, string output) Sign(string exePath, string privateKey, string pbo)
    {
        try
        {
            using var paths = new NativePathScope();
            var r = ProcRunner.Run(exePath, SignArgs(paths.FilePath(privateKey), paths.FilePath(pbo)),
                new RunOpts(TimeoutMs: 120_000, WorkingDir: paths.DirectoryPath(Path.GetDirectoryName(Path.GetFullPath(pbo))!)));
            var signature = pbo + "." + Path.GetFileNameWithoutExtension(privateKey) + ".bisign";
            return (r.Ok && File.Exists(signature) && new FileInfo(signature).Length > 0, r.AllOutput);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
