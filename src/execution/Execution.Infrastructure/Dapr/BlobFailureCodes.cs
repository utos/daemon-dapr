using Common.Blobs;
using Execution.Infrastructure.Dapr.ActivityHandlers;

namespace Execution.Infrastructure.Dapr;

/// <summary>
/// The binary-data failure codes, recovered from a failure that has crossed the Dapr boundary.
/// <para>
/// A Dapr activity's exception reaches the orchestrator flattened to a message — the type is gone —
/// so a code that must survive has either to be returned as data or to be recognisable in the text.
/// <see cref="ActivityHandlers.ResponseTooLargeException"/> and
/// <see cref="BlobStoreUnavailableException"/> are raised deep inside the HTTP handler, where
/// returning data would mean threading a failure shape through every layer between; recognising
/// them here is the smaller change and keeps the code on the failure the author sees.
/// </para>
/// <para>
/// Both are catchable by an <c>onFailure</c> rule at the activity where they occur, which is the
/// point of them being distinct codes: a rule can tell "the body was too big" from "there is nowhere
/// to put bytes" from any other reason an activity failed, without an error-code taxonomy for HTTP
/// statuses.
/// </para>
/// </summary>
internal static class Blobs
{
    /// <summary>
    /// The <c>UTOS-F1##</c> code this failure is, or null when it is not one of them.
    /// </summary>
    internal static string? CodeFor(Exception ex) => ex switch
    {
        ResponseTooLargeException => ResponseTooLargeException.Code,
        BlobStoreUnavailableException => BlobStoreUnavailableException.Code,
        BlobUnreadableException => BlobUnreadableException.Code,
        BlobsNotAdmittedException => BlobsNotAdmittedException.Code,

        // Flattened by the Dapr boundary: the type is gone and the message is what is left. Matched
        // on the code itself, which each exception's own message does not carry — so it is matched
        // on the sentence, and the sentences are asserted in the tests that raise them.
        _ when ex.Message.Contains("exceeded the maximum of", StringComparison.Ordinal)
            => ResponseTooLargeException.Code,
        _ when ex.Message.Contains("no blob store is configured", StringComparison.Ordinal)
            => BlobStoreUnavailableException.Code,

        // The exception this one *does* lead its message with the code, because it is the only one
        // raised where a sentence would not have survived: a spawn's refusal is written by the blobs
        // module and read by an `onFailure` rule on the other side of two boundaries.
        _ when ex.Message.Contains(BlobsNotAdmittedException.Code, StringComparison.Ordinal)
            => BlobsNotAdmittedException.Code,

        _ => null,
    };
}
