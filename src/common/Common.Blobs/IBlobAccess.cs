using Common.SeedWork;
using Common.Workflow;

namespace Common.Blobs;

/// <summary>
/// What a run does with blobs, as the contract between two modules: the blobs module publishes it,
/// the execution module consumes it.
/// <para>
/// In <c>common/</c> for the reason <c>Common.Workflow.Resolution</c> and
/// <c>Common.ExecutionLifecycle</c> are: modules do not reference each other, so anything two of
/// them must agree on lives here. Execution needs to turn a response body into a value and a handle
/// back into bytes; it must not need to know what a blob record is, which run trees it belongs to,
/// or where the object store is.
/// </para>
/// </summary>
public interface IBlobAccess
{
    /// <summary>
    /// Bodies at or below this are carried <b>inline</b> — the bytes travel in the value — and
    /// anything larger is <b>stored</b>, the value becoming a handle.
    /// <para>
    /// Small, and deliberately so. Every inline byte is copied at every hop and written to history:
    /// in this daemon a response reaches the interpreter as a Dapr activity input, so an inline body
    /// is serialized into the orchestration history on <em>every transition</em> of the run. That is
    /// durable growth, not a memory cost, which is why the threshold is a few hundred kibibytes
    /// rather than the megabytes a response cap allows.
    /// </para>
    /// </summary>
    long InlineThreshold { get; }

    /// <summary>
    /// Whether a store is configured at all. False is a supported deployment: inline blobs still
    /// work and anything larger is <c>UTOS-F102</c>.
    /// </summary>
    bool CanStore { get; }

    /// <summary>
    /// Stores bytes a run produced and returns the handle. The blob is <b>intermediate</b> — nobody
    /// has asked for it yet — and belongs to <paramref name="provenance"/>'s run tree.
    /// </summary>
    /// <exception cref="BlobStoreUnavailableException">
    /// No store is configured, so the bytes have nowhere to go — <c>UTOS-F102</c>.
    /// </exception>
    Task<WorkflowBlob> ProduceAsync(
        Stream content,
        string mediaType,
        string? name,
        BlobUsage provenance,
        CancellationToken cancellationToken);

    /// <summary>
    /// The bytes of <paramref name="blob"/>'s window, for an execution in
    /// <paramref name="runTree"/>.
    /// <para>
    /// The access rule is enforced here and nowhere else: an execution may read a stored blob only
    /// if the blob belongs to its run tree. A handle naming any other object fails exactly as a
    /// deleted one does, which is what makes forging an id useless — it buys a failure, not bytes,
    /// and not even the knowledge that the id exists.
    /// </para>
    /// </summary>
    /// <exception cref="BlobUnreadableException">Any reason the bytes did not arrive — <c>UTOS-F103</c>.</exception>
    Task<byte[]> ReadAsync(WorkflowBlob blob, ExecutionId runTree, CancellationToken cancellationToken);
}

/// <summary>
/// What an activity was doing when it touched a blob, for the history row it writes.
/// <para>
/// Carried as data rather than as references. A durable blob outlives its run, and deleting the
/// execution must not erase the provenance that made the blob worth keeping.
/// </para>
/// <para>
/// In <c>common/</c> rather than the blobs module because both sides need it: execution supplies it
/// when a run produces a blob, and the blobs module folds it into a history row.
/// </para>
/// </summary>
/// <param name="RootExecution">
/// The root of the run tree. It is what a history row is keyed by, and what decides which tree the
/// blob belongs to — so a value produced deep in a fan-out is attached once, to the tree, rather
/// than to whichever child happened to make it.
/// </param>
public sealed record BlobUsage(
    ExecutionId Execution,
    ExecutionId RootExecution,
    string Workflow,
    string? WorkflowDigest = null,
    string? Activity = null);

/// <summary>
/// A blob had to be stored and no store is configured — <c>UTOS-F102</c>. Distinct from
/// <see cref="BlobUnreadableException"/>: nothing is wrong with the blob or the request, the daemon
/// simply has nowhere to put bytes, and an operator rather than an author has to act.
/// </summary>
public sealed class BlobStoreUnavailableException(string message) : Exception(message)
{
    public const string Code = "UTOS-F102";
}

/// <summary>
/// A blob's bytes could not be read — <c>UTOS-F103</c>. One exception for every reason on purpose:
/// deleted, not this run tree's, or the store failed after its own retries all have to be
/// indistinguishable, or the difference would tell a caller which ids exist.
/// </summary>
public sealed class BlobUnreadableException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public const string Code = "UTOS-F103";
}
