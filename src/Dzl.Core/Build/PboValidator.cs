using System.Security.Cryptography;
using System.Text;

namespace Dzl.Core.Build;

/// <summary>Validate archives produced by the stored-entry writer before they become build output.
/// Checks structure and the container checksum, not a BI cryptographic signature.</summary>
public static class PboValidator
{
    /// <summary>Validate the container checksum independently of the stored/compressed entry method.</summary>
    public static bool HasValidChecksum(string path)
        => VerifiedDigest(path) is not null;

    internal static byte[]? VerifiedDigest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 21) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buffer = new byte[1024 * 1024];
            long remaining = stream.Length - 21;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) return null;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (stream.ReadByte() != 0) return null;
            var digest = new byte[20];
            stream.ReadExactly(digest);
            var computed = hash.GetHashAndReset();
            return CryptographicOperations.FixedTimeEquals(computed, digest) ? computed : null;
        }
        catch { return null; }
    }

    public static (bool ok, string error) Validate(string path, string expectedPrefix)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            string CString()
            {
                var bytes = new List<byte>();
                for (int i = 0; i < 4096; i++)
                {
                    var b = reader.ReadByte();
                    if (b == 0) return Encoding.ASCII.GetString(bytes.ToArray());
                    bytes.Add(b);
                }
                throw new InvalidDataException("unterminated PBO string");
            }
            void Require(bool condition, string error)
            { if (!condition) throw new InvalidDataException(error); }

            Require(CString() == "" && reader.ReadUInt32() == 0x56657273, "missing version header");
            for (int i = 0; i < 4; i++) Require(reader.ReadUInt32() == 0, "invalid version fields");
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var key = CString(); key.Length > 0; key = CString())
                Require(properties.TryAdd(key, CString()), "duplicate property");
            var prefix = expectedPrefix.Replace('/', '\\').Trim('\\');
            Require(properties.GetValueOrDefault("prefix", "") == prefix, "prefix mismatch");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long payload = 0;
            long prefixOffset = -1;
            uint prefixSize = 0;
            while (true)
            {
                var name = CString();
                var method = reader.ReadUInt32();
                var original = reader.ReadUInt32();
                var reserved = reader.ReadUInt32();
                var timestamp = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                if (name.Length == 0)
                {
                    Require(method == 0 && original == 0 && reserved == 0 && timestamp == 0 && size == 0, "invalid terminator");
                    break;
                }
                Require(method == 0 && original == size, "invalid stored entry length/method");
                Require(!name.StartsWith('\\') && !name.Contains(':') &&
                    !name.Split('\\', '/').Any(s => s is ".." or "." or ""), "unsafe entry name");
                Require(names.Add(name), "duplicate entry name");
                if (name.Equals("$PBOPREFIX$", StringComparison.OrdinalIgnoreCase))
                { prefixOffset = payload; prefixSize = size; }
                payload = checked(payload + size);
                Require(payload <= stream.Length, "entry payload exceeds archive length");
            }
            var dataStart = stream.Position;
            Require(checked(dataStart + payload + 21) == stream.Length, "payload length/trailer mismatch");
            if (prefix.Length > 0)
            {
                var expected = Encoding.ASCII.GetBytes(prefix + "\r\n");
                Require(prefixOffset >= 0 && prefixSize == expected.Length, "missing prefix payload");
                stream.Position = dataStart + prefixOffset;
                Require(reader.ReadBytes(expected.Length).SequenceEqual(expected), "prefix payload mismatch");
            }
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buffer = new byte[1024 * 1024];
            var remaining = stream.Length - 21;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            Require(reader.ReadByte() == 0, "invalid checksum separator");
            Require(CryptographicOperations.FixedTimeEquals(reader.ReadBytes(20), hash.GetHashAndReset()), "checksum mismatch");
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
