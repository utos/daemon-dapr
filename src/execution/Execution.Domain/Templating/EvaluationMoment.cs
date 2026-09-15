using System.Security.Cryptography;

namespace Execution.Domain.Templating;

/// <summary>
/// The instant and the seed an evaluation's expressions read time and randomness from —
/// <c>Date.now()</c>, <c>new Date()</c>, <c>Math.random()</c> and <c>crypto.randomUUID()</c>, as
/// <c>api/docs/template-expressions.md</c> § Node.js globals defines them.
/// <para>
/// Captured <b>once per activity evaluation</b>, inside the Dapr activity that evaluates, whose
/// result the runtime records: that is what makes every expression of one decision agree, and a
/// retry or a replay agree with the first run, without the engine ever touching a clock. The seed
/// is drawn from the platform CSPRNG, never derived from an id, a clock or a counter — the spec
/// requires it, because the derivations are a hash-based DRBG and a guessable seed would make every
/// value predictable. Not <c>WorkflowContext.NewGuid()</c>, which is exactly such a derivation.
/// </para>
/// </summary>
public sealed record EvaluationMoment(DateTimeOffset Instant, Guid Seed)
{
    /// <summary>
    /// The moment an evaluation with no caller-supplied one runs at: the Unix epoch and the nil
    /// UUID. Fixed rather than "now", so a unit test that never thinks about time is still
    /// deterministic, and so an engine can never fall back to the wall clock by omission.
    /// </summary>
    public static EvaluationMoment Unset { get; } = new(DateTimeOffset.UnixEpoch, Guid.Empty);

    /// <summary>A fresh moment: the wall clock, and 128 bits from the CSPRNG shaped as a v4 UUID.</summary>
    public static EvaluationMoment Capture()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        // Version 4, RFC 9562 variant — so the seed reads as an ordinary random UUID wherever it
        // is shown, and Guid's constructor takes it as the bytes it is rather than reordering.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new EvaluationMoment(DateTimeOffset.UtcNow, new Guid(bytes, bigEndian: true));
    }
}
