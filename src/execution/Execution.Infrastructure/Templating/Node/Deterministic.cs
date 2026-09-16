using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// The derivations of <c>api/docs/template-expressions.md</c> § Node.js globals, exactly as
/// written there, so that a second implementation computes the same values from the same seed —
/// the evaluation corpus pins both. A hash-based DRBG: earlier outputs reveal neither the seed nor
/// later outputs, given a seed the executor drew from a CSPRNG (<see cref="Domain.Templating.EvaluationMoment"/>).
/// </summary>
internal static class Deterministic
{
    /// <summary>
    /// The <paramref name="n"/>-th <c>Math.random()</c>: the first 53 bits of
    /// <c>SHA-256(seed ‖ n)</c> as a fraction in [0, 1), with the seed as its 16 bytes and
    /// <paramref name="n"/> as an 8-byte big-endian integer.
    /// </summary>
    public static double Random(Guid seed, long n)
    {
        Span<byte> input = stackalloc byte[24];
        seed.TryWriteBytes(input, bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], n);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);

        var bits = BinaryPrimitives.ReadUInt64BigEndian(digest) >> 11;
        return bits / 9007199254740992.0; // 2^53
    }

    /// <summary>
    /// The <paramref name="n"/>-th <c>crypto.randomUUID()</c>: UUID version 5 (RFC 9562 § 5.5)
    /// with the seed as namespace and the decimal string of <paramref name="n"/> as name.
    /// </summary>
    public static string Uuid(Guid seed, long n)
    {
        var name = Encoding.ASCII.GetBytes(n.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Span<byte> input = stackalloc byte[16 + 20];
        seed.TryWriteBytes(input, bigEndian: true, out _);
        name.CopyTo(input[16..]);

        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(input[..(16 + name.Length)], digest);

        digest[6] = (byte)((digest[6] & 0x0F) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(digest[..16]).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
