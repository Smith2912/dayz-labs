using System.Text;
using Dzl.Core.App;
using Dzl.Core.Build;
using Dzl.Core.Procs;
using Dzl.Core.Tools;
using FluentAssertions;

/// <summary>Explicit opt-in native tool tests. Inputs, private test keys and output stay in an isolated artifact root.</summary>
public class NativeToolIntegrationTests
{
    private static string Tools => Environment.GetEnvironmentVariable("DZL_NATIVE_TOOLS")!;
    private static string Root(string scenario)
    {
        var parent = Environment.GetEnvironmentVariable("DZL_NATIVE_ARTIFACTS") ?? Path.GetTempPath();
        var root = Path.Combine(parent, scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static string Tool(string name) => ToolCatalog.Find(Tools, name)!.ExePath;
    private static void Log(string root, string name, string output) => File.WriteAllText(Path.Combine(root, name + ".log"), output);
    private const string Config = "class CfgPatches { class DZL_NativeProbe { units[] = {}; weapons[] = {}; requiredVersion = 0.1; requiredAddons[] = {}; }; };";

    [NativeToolsFact]
    public void FileBank_and_AddonBuilder_pack_disposable_sources()
    {
        var root = Root("native-packers");
        var source = Directory.CreateDirectory(Path.Combine(root, "probe")).FullName;
        File.WriteAllText(Path.Combine(source, "config.cpp"), Config);
        foreach (var key in new[] { "filebank", "addonbuilder" })
        {
            var output = Directory.CreateDirectory(Path.Combine(root, key)).FullName;
            bool ok;
            string log;
            if (key == "filebank") (ok, log) = FileBank.Pack(Tool(key), source, output, "DZL_NativeProbe");
            else
            {
                var result = AddonBuilder.Pack(Tool(key), source, output, packOnly: true, prefix: "DZL_NativeProbe",
                    tempDir: Path.Combine(root, "temp"));
                ok = result.Ok; log = result.Output;
            }
            Log(root, key, log);
            ok.Should().BeTrue(log);
            var pbo = Path.Combine(output, "probe.pbo");
            PboValidator.HasValidChecksum(pbo).Should().BeTrue(log);
            PboHeader.Read(pbo)!.Entries.Should().Contain(e => e.Name == "config.cpp");
        }
    }

    [NativeToolsFact]
    public void ImageToPaa_converts_an_original_tga_texture()
    {
        var root = Root("imagetopaa");
        var input = Path.Combine(root, "probe_co.tga");
        using (var writer = new BinaryWriter(File.Create(input)))
        {
            writer.Write(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            writer.Write((short)16); writer.Write((short)16); writer.Write((byte)32); writer.Write((byte)8);
            for (int i = 0; i < 256; i++) writer.Write(new byte[] { 32, 64, 128, 255 });
        }
        var result = ImageToPaa.ConvertFolder(Tool("imagetopaa"), root, false).Single();
        Log(root, "convert", result.Message);
        result.Ok.Should().BeTrue(result.Message);
        new FileInfo(result.Output).Length.Should().BeGreaterThan(18);
    }

    [NativeToolsFact]
    public void CfgConvert_roundtrips_valid_config_and_rejects_invalid_input()
    {
        var root = Root("cfgconvert");
        var cpp = Path.Combine(root, "config.cpp");
        var bin = Path.Combine(root, "config.bin");
        File.WriteAllText(cpp, Config);
        var result = CfgConvert.ToBin(Tool("cfgconvert"), cpp, bin);
        Log(root, "to-bin", result.output);
        result.ok.Should().BeTrue(result.output);
        File.Exists(bin).Should().BeTrue();
        var decoded = Path.Combine(root, "decoded.cpp");
        var roundtrip = CfgConvert.Unbinarize(Tool("cfgconvert"), bin, decoded);
        Log(root, "to-text", roundtrip.output);
        roundtrip.ok.Should().BeTrue(roundtrip.output);
        File.ReadAllText(decoded).Should().Contain("DZL_NativeProbe");
        File.WriteAllText(cpp, "class CfgPatches { class Broken { units[] = { ;");
        var invalid = CfgConvert.ToBin(Tool("cfgconvert"), cpp, Path.Combine(root, "invalid.bin"));
        Log(root, "invalid", invalid.output);
        invalid.ok.Should().BeFalse("the wrapper must not report a native parser error as success");
        var failedBuild = BuildEngine.Run(Tools, root, "DZL_NativeProbe", "invalid", Path.Combine(Root("invalid-work"), "work"),
            Root("invalid-output"), true, null, null, root);
        failedBuild.Ok.Should().BeFalse("a requested binary conversion must not silently fall back to raw invalid config.cpp");
    }

    [NativeToolsFact]
    public void BankRev_roundtrips_our_archive_and_repairs_corrupt_outputs()
    {
        var root = Root("bankrev");
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        File.WriteAllText(Path.Combine(source, "config.cpp"), Config);
        File.WriteAllText(Path.Combine(source, "payload.txt"), "original-payload");
        var pbo = Path.Combine(game, "probe.pbo");
        PboWriter.Pack(source, pbo, "DZL_NativeProbe").ok.Should().BeTrue();
        var lines = new List<string>();
        var first = GameUnpack.UnpackAll(Tool("bankrev"), game, dest, false, item => lines.Add(item.Status));
        Log(root, "first-extraction", first.Message + "\n" + string.Join("\n", lines));
        first.Ok.Should().BeTrue(first.Message);
        var payload = Path.Combine(dest, "DZL_NativeProbe", "payload.txt");
        File.ReadAllText(payload).Should().Be("original-payload");
        GameUnpack.UnpackAll(Tool("bankrev"), game, dest, false).Skipped.Should().Be(1);
        File.WriteAllText(payload, "corrupted-output");
        GameUnpack.UnpackAll(Tool("bankrev"), game, dest, false).Extracted.Should().Be(1);
        File.ReadAllText(payload).Should().Be("original-payload");
        File.Delete(payload);
        GameUnpack.UnpackAll(Tool("bankrev"), game, dest, true).Ok.Should().BeTrue();
        File.ReadAllText(payload).Should().Be("original-payload");
    }

    [NativeToolsFact]
    public void Native_signing_verifies_the_generated_pbo_and_rejects_a_tampered_payload()
    {
        var root = Root("signing");
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var keys = Directory.CreateDirectory(Path.Combine(root, "keys")).FullName;
        var addons = Directory.CreateDirectory(Path.Combine(root, "addons")).FullName;
        File.WriteAllText(Path.Combine(source, "config.cpp"), Config);
        File.WriteAllText(Path.Combine(source, "payload.p3d"), "original-payload");
        var pbo = Path.Combine(addons, "probe.pbo");
        PboWriter.Pack(source, pbo, "DZL_NativeProbe").ok.Should().BeTrue();
        var key = DsTools.CreateKey(Tool("dscreatekey"), keys, "DZL_DisposableNativeProbe");
        Log(root, "keygen", key.Output);
        key.Ok.Should().BeTrue(key.Output);
        File.Exists(key.PublicKey).Should().BeTrue();
        var signed = DsTools.Sign(Tool("dssignfile"), key.PrivateKey, pbo);
        Log(root, "sign", signed.output);
        signed.ok.Should().BeTrue(signed.output);
        var checker = Path.Combine(Tools, "Bin", "DsUtils", "DSCheckSignatures.exe");
        var verified = DsTools.CheckSignatures(checker, addons, keys);
        Log(root, "verify-valid", verified.output);
        verified.ok.Should().BeTrue(verified.output);
        BiSignature.VerifyPayload(pbo, PboHeader.FindSignature(pbo)!, new[] { key.PublicKey }).Should().BeTrue();
        DsTools.CreateKey(Tool("dscreatekey"), keys, "DZL_DisposableNativeProbe").Ok.Should().BeFalse("existing keys must be preserved");
        var signedEngine = BuildEngine.Run(Tools, source, "DZL_NativeProbe", "engine-probe", Path.Combine(root, "engine-work"),
            Path.Combine(root, "engine-addons"), false, key.PrivateKey, null);
        signedEngine.Ok.Should().BeTrue(signedEngine.Output);
        var entry = PboHeader.Read(pbo)!.Entries.Single(e => e.Name == "payload.p3d");
        using (var file = new FileStream(pbo, FileMode.Open, FileAccess.ReadWrite))
        { file.Position = entry.Offset; file.WriteByte((byte)'X'); }
        var rejected = DsTools.CheckSignatures(checker, addons, keys);
        Log(root, "verify-tampered", rejected.output);
        rejected.ok.Should().BeFalse("native signature validation must reject a modified payload");
        var bytes = File.ReadAllBytes(pbo);
        System.Security.Cryptography.SHA1.HashData(bytes.AsSpan(0, bytes.Length - 21)).CopyTo(bytes.AsSpan(bytes.Length - 20));
        File.WriteAllBytes(pbo, bytes);
        var wrongSignature = DsTools.CheckSignatures(checker, addons, keys);
        Log(root, "verify-resigned-container", wrongSignature.output);
        wrongSignature.ok.Should().BeFalse("even a valid container checksum must not make a stale BI signature valid");
    }

    [NativeToolsFact]
    public void Binarize_produces_odol_and_the_engine_packages_it_with_a_binary_config()
    {
        var root = Root("binarize");
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var context = Directory.CreateDirectory(Path.Combine(root, "context")).FullName;
        var bin = Path.Combine(Tools, "Bin", "Binarize", "bin");
        foreach (var file in Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(context, "bin", Path.GetRelativePath(bin, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(source, "config.cpp"), Config);
        WriteTriangle(Path.Combine(source, "triangle.p3d"));
        var log = new List<string>();
        var result = BuildEngine.Run(Tools, source, "DZL_NativeProbe", "probe", Path.Combine(root, "work"),
            Path.Combine(root, "addons"), true, null, log.Add, context, new[] { context });
        Log(root, "engine", string.Join("\n", log) + "\n" + result.Output);
        result.Ok.Should().BeTrue(result.Output);
        var header = PboHeader.Read(result.Pbo)!;
        header.Entries.Should().Contain(e => e.Name == "config.bin");
        var model = header.Entries.Single(e => e.Name == "triangle.p3d");
        using var stream = File.OpenRead(result.Pbo);
        stream.Position = model.Offset;
        var magic = new byte[4];
        stream.ReadExactly(magic);
        Encoding.ASCII.GetString(magic).Should().Be("ODOL", "Binarize must actually convert the model");
        PboValidator.Validate(result.Pbo, "DZL_NativeProbe").ok.Should().BeTrue();
    }

    // Original synthetic geometry, no vanilla assets, textures, scripts, or gameplay dependencies.
    private static void WriteTriangle(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("MLOD")); writer.Write(257); writer.Write(1);
        writer.Write(Encoding.ASCII.GetBytes("P3DM")); writer.Write(28); writer.Write(256);
        writer.Write(3); writer.Write(1); writer.Write(1); writer.Write(0);
        foreach (var point in new[] { (0f, 0f, 0f), (1f, 0f, 0f), (0f, 1f, 0f) })
        { writer.Write(point.Item1); writer.Write(point.Item2); writer.Write(point.Item3); writer.Write(0); }
        writer.Write(0f); writer.Write(0f); writer.Write(1f);
        writer.Write(3);
        for (int i = 0; i < 4; i++) { writer.Write(i % 3); writer.Write(0); writer.Write(0f); writer.Write(0f); }
        writer.Write(0); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(Encoding.ASCII.GetBytes("TAGG")); writer.Write((byte)1);
        writer.Write(Encoding.ASCII.GetBytes("#EndOfFile#\0")); writer.Write(0); writer.Write(1f);
    }
}

public sealed class NativeToolsFactAttribute : FactAttribute
{
    public NativeToolsFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DZL_NATIVE_TOOLS")))
            Skip = "Opt-in native test: set DZL_NATIVE_TOOLS and optionally DZL_NATIVE_ARTIFACTS.";
    }
}
