using System.Security.Cryptography;
using System.Text;
using Dzl.Core.Build;
using Dzl.Core.Tools;
using FluentAssertions;

public class NativeBoundaryRegressionTests
{
    [Fact]
    public void Ascii_aliases_preserve_unicode_targets_and_cleanup_only_owned_links()
    {
        var root = Directory.CreateTempSubdirectory("dzl-native-path-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "workspace-🖥")).FullName;
        var input = Path.Combine(source, "config.cpp");
        File.WriteAllText(input, "preserve");
        string alias;
        using (var paths = new NativePathScope())
        {
            alias = paths.FilePath(input);
            if (OperatingSystem.IsWindows()) alias.ToCharArray().Should().OnlyContain(c => c <= 127);
            File.ReadAllText(alias).Should().Be("preserve");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(alias)!, "output.bin"), "written");
        }
        File.ReadAllText(input).Should().Be("preserve");
        File.ReadAllText(Path.Combine(source, "output.bin")).Should().Be("written");
        if (OperatingSystem.IsWindows()) File.Exists(alias).Should().BeFalse();
    }

    [Fact]
    public void Native_aliases_reject_shell_metacharacters_before_link_creation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("dzl-native-path-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "unicode-🖥&literal")).FullName;
        using var paths = new NativePathScope();
        var action = () => paths.DirectoryPath(source);
        action.Should().Throw<IOException>().WithMessage("*metacharacters*");
        Directory.Exists(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("payload", false)]
    [InlineData("repaired-checksum", false)]
    [InlineData("signature", false)]
    [InlineData("untrusted-key", false)]
    public void Bi_signature_binds_all_payload_bytes_to_a_trusted_public_key(string change, bool valid)
    {
        var root = Directory.CreateTempSubdirectory("dzl-bi-signature-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "payload.p3d"), "signed-content");
        var pbo = Path.Combine(root, "probe.pbo");
        PboWriter.Pack(source, pbo, "NativeProbe").ok.Should().BeTrue();
        using var rsa = RSA.Create(1024);
        var key = rsa.ExportParameters(false);
        using var blobStream = new MemoryStream();
        using (var writer = new BinaryWriter(blobStream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(new byte[] { 6, 2, 0, 0 });
            writer.Write(0x2400); writer.Write(0x31415352); writer.Write(1024); writer.Write(65537);
            writer.Write(key.Modulus!.Reverse().ToArray());
        }
        var blob = blobStream.ToArray();
        var publicKey = Path.Combine(root, "probe.bikey");
        var signature = pbo + ".probe.bisign";
        void WritePublic(BinaryWriter writer)
        { writer.Write(Encoding.ASCII.GetBytes("NativeProbe\0")); writer.Write(blob.Length); writer.Write(blob); }
        using (var writer = new BinaryWriter(File.Create(publicKey))) WritePublic(writer);
        var signed = rsa.SignHash(PboValidator.VerifiedDigest(pbo)!, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        Array.Reverse(signed);
        using (var writer = new BinaryWriter(File.Create(signature)))
        { WritePublic(writer); writer.Write(signed.Length); writer.Write(signed); }

        if (change is "payload" or "repaired-checksum")
        {
            var bytes = File.ReadAllBytes(pbo);
            var offset = PboHeader.Read(pbo)!.Entries.Single(e => e.Name == "payload.p3d").Offset;
            bytes[(int)offset] ^= 0x40;
            if (change == "repaired-checksum")
                SHA1.HashData(bytes.AsSpan(0, bytes.Length - 21)).CopyTo(bytes.AsSpan(bytes.Length - 20));
            File.WriteAllBytes(pbo, bytes);
        }
        if (change == "signature")
        { var bytes = File.ReadAllBytes(signature); bytes[^1] ^= 0x40; File.WriteAllBytes(signature, bytes); }
        BiSignature.VerifyPayload(pbo, signature, change == "untrusted-key" ? Array.Empty<string>() : new[] { publicKey })
            .Should().Be(valid);
    }
}
