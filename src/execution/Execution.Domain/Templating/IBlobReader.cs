using Common.Workflow;

namespace Execution.Domain.Templating;

/// <summary>
/// Reads the bytes a blob stands for, for the one place in the language that needs them:
/// <c>bytes()</c> and <c>text()</c> in an expression.
/// <para>
/// <b>Synchronous, deliberately.</b> <c>api/docs/template-expressions.md</c> § Awaiting says a read
/// happens when the member is called and the promise it returns is already settled — so an
/// expression's reads happen in program order whatever it awaits and when, and two evaluations of
/// one expression read the same bytes in the same order. A blob's immutability makes those the
/// same bytes, and replay needs nothing more. An asynchronous read would let the engine interleave
/// and would cost exactly that property.
/// </para>
/// <para>
/// An inline blob needs no reader — it carries its bytes. This exists for a stored one, whose
/// bytes live in the object store and whose window is the handle's offset and size.
/// </para>
/// </summary>
public interface IBlobReader
{
    /// <summary>
    /// The bytes of <paramref name="blob"/>'s window: <c>size</c> bytes from <c>offset</c>.
    /// </summary>
    /// <exception cref="BlobUnreadableException">
    /// The blob is gone, does not belong to this run tree, or the store failed after the
    /// implementation's own retries — all <c>UTOS-F103</c>, which is the point of the single
    /// exception: a handle naming another tree's object must fail exactly as a deleted one does,
    /// or the difference would tell an attacker which ids exist.
    /// </exception>
    byte[] Read(WorkflowBlob blob);
}

/// <summary>A blob's bytes could not be read — <c>UTOS-F103</c>.</summary>
public sealed class BlobUnreadableException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public const string Code = "UTOS-F103";
}
