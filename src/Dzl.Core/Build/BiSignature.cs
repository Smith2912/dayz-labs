using System.Security.Cryptography;
using System.Text;

namespace Dzl.Core.Build;

/// <summary>Bind the first BI RSA/SHA-1 signature to a freshly computed full PBO digest and a
/// trusted public .bikey. Native DSCheckSignatures remains an additional format/compatibility check.</summary>
public static class BiSignature
{
    public static bool VerifyPayload(string pbo, string signature, IEnumerable<string> trustedKeys)
    {
        try
        {
            var digest = PboValidator.VerifiedDigest(pbo);
            if (digest is null) return false;
            using var reader = new BinaryReader(File.OpenRead(signature), Encoding.ASCII);
            var (author, blob, parameters) = ReadKey(reader);
            var size = reader.ReadInt32();
            if (size != parameters.Modulus!.Length) return false;
            var signed = reader.ReadBytes(size);
            if (signed.Length != size) return false;
            Array.Reverse(signed); // CryptoAPI stores RSA integers little-endian
            var trusted = trustedKeys.Any(path =>
            {
                try
                {
                    using var key = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
                    var candidate = ReadKey(key);
                    return candidate.Author == author && candidate.Blob.SequenceEqual(blob);
                }
                catch { return false; }
            });
            if (!trusted) return false;
            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            return rsa.VerifyHash(digest, signed, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    private static (string Author, byte[] Blob, RSAParameters Parameters) ReadKey(BinaryReader reader)
    {
        var author = new List<byte>();
        for (int i = 0; ; i++)
        {
            if (i > 1024) throw new InvalidDataException("Invalid BI key author");
            var b = reader.ReadByte();
            if (b == 0) break;
            author.Add(b);
        }
        var size = reader.ReadInt32();
        if (size < 20 || size > 16384) throw new InvalidDataException("Invalid BI public key size");
        var blob = reader.ReadBytes(size);
        if (blob.Length != size || blob[0] != 6 || blob[1] != 2 || BitConverter.ToUInt32(blob, 8) != 0x31415352)
            throw new InvalidDataException("Invalid RSA public blob");
        var bits = BitConverter.ToUInt32(blob, 12);
        if (bits < 1024 || bits > 8192 || bits % 8 != 0 || bits / 8 + 20 != size)
            throw new InvalidDataException("Invalid BI RSA modulus");
        var modulus = blob[20..];
        Array.Reverse(modulus);
        var exponent = blob[16..20].Reverse().SkipWhile(b => b == 0).ToArray();
        if (exponent.Length == 0) throw new InvalidDataException("Invalid BI RSA exponent");
        return (Encoding.ASCII.GetString(author.ToArray()), blob, new RSAParameters { Modulus = modulus, Exponent = exponent });
    }
}
