namespace Blobs.Domain;

/// <summary>
/// How a run tree ended, as the blobs module needs to know it.
/// <para>
/// Three cases and not a boolean, because the spec's rule is written over exactly this distinction:
/// a completed tree's intermediates can go at once, and a failed or cancelled one's are kept for a
/// window because they are precisely what an operator wants to look at. Deleting them at the moment
/// of failure is how the evidence is lost.
/// </para>
/// </summary>
public enum RunTreeOutcome
{
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// When the daemon may remove a blob nobody asked for — <c>api/docs/binary-data.md</c>
/// § When intermediates go.
/// <para>
/// The spec's hard requirement is one sentence: a blob must stay readable while any run tree it
/// belongs to is not terminal, retries included. Everything below is the recommended shape, and every
/// duration is configuration. Removing a blob earlier than the requirement allows is not permitted;
/// removing one later always is, which is why every window here errs long.
/// </para>
/// </summary>
/// <param name="FailedRunWindow">
/// How long a failed or cancelled tree's intermediates are kept. They are the evidence.
/// </param>
/// <param name="AbandonedUploadWindow">
/// How long a <c>CreateBlob</c> that was never completed is left alone. A client may be uploading a
/// very large file in parts, so this is not short.
/// </param>
/// <param name="InterruptedRunWindow">
/// The backstop, and the only rule here that can act on a tree whose ending the daemon never
/// recorded — a crash, a lifecycle event that parked, an execution deleted without notice. It is the
/// one rule that could in principle remove a blob a live run could still read, so it is measured
/// from the moment the blob was attached and defaults to long enough that no ordinary run reaches
/// it. A workflow that legitimately waits weeks needs this raised.
/// </param>
/// <param name="GracePeriod">
/// The youngest a blob may be and still be considered at all. Producing a blob is three writes — the
/// record, the run-tree attachment, the history row — so for a few milliseconds a real blob has no
/// run tree and looks exactly like one nothing can reach. Rather than widen the write into a
/// transaction that <c>AttachToRunTreeAsync</c>'s race handling would have to be folded into, the
/// sweep declines to look at anything this new.
/// </param>
public sealed record BlobRetentionPolicy(
    TimeSpan FailedRunWindow,
    TimeSpan AbandonedUploadWindow,
    TimeSpan InterruptedRunWindow,
    TimeSpan GracePeriod)
{
    /// <summary>The defaults, stated once so a test and a deployment start from the same place.</summary>
    public static BlobRetentionPolicy Default { get; } = new(
        FailedRunWindow: TimeSpan.FromDays(7),
        AbandonedUploadWindow: TimeSpan.FromDays(1),
        InterruptedRunWindow: TimeSpan.FromDays(30),
        GracePeriod: TimeSpan.FromMinutes(10));
}
