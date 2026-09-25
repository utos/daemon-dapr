using Common.Blobs;
using Common.SeedWork;

namespace Blobs.Domain;

/// <summary>
/// The daemon's record of what blobs exist, who may read them, and what has happened to them.
/// <para>
/// Separate from <c>IObjectStore</c>, which holds bytes and knows nothing else. This is what turns
/// an id — which is not an address — into a key the store understands, and it is the only thing
/// that can answer whether a given run may read a given blob.
/// </para>
/// </summary>
public interface IBlobRepository
{
    Task<StoredBlob?> FindAsync(BlobId id, CancellationToken cancellationToken);

    /// <summary>Several at once, for checking every blob in a run's input in one round trip.</summary>
    Task<IReadOnlyDictionary<BlobId, StoredBlob>> FindManyAsync(
        IReadOnlyCollection<BlobId> ids, CancellationToken cancellationToken);

    Task AddAsync(StoredBlob blob, CancellationToken cancellationToken);

    Task UpdateAsync(StoredBlob blob, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this blob belongs to the run tree rooted at <paramref name="rootExecution"/> — the
    /// whole of the spec's access rule. An execution may read a stored blob only if the blob belongs
    /// to its run tree; a handle naming anything else fails exactly as a deleted blob does, which is
    /// what makes an id prove nothing.
    /// <para>
    /// A question rather than a loaded collection: a durable upload may be attached to any number of
    /// run trees, so the membership set is unbounded and reading it to answer one check would be the
    /// wrong shape.
    /// </para>
    /// </summary>
    Task<bool> BelongsToRunTreeAsync(
        BlobId id, ExecutionId rootExecution, CancellationToken cancellationToken);

    /// <summary>
    /// Records that a blob belongs to a run tree — because it was produced there, attached by a
    /// client at schedule, or carried into a new tree by a <c>workflow.spawn</c>. Attaching one that
    /// is already a member changes nothing.
    /// </summary>
    Task AttachToRunTreeAsync(
        BlobId id, ExecutionId rootExecution, CancellationToken cancellationToken);

    /// <summary>
    /// Appends to a blob's timeline, folding into an existing row when one already has the same
    /// (kind, root execution, workflow) — see <see cref="BlobHistoryEntry"/> for why that
    /// aggregation is the design rather than an optimization.
    /// </summary>
    Task RecordEventAsync(
        BlobId id,
        BlobEventKind kind,
        DateTimeOffset at,
        BlobUsage? usage,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BlobHistoryEntry>> HistoryAsync(
        BlobId id, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the run tree rooted at <paramref name="rootExecution"/> reached a terminal state,
    /// against every blob attached to it. This is the accurate signal the sweep runs on; without it
    /// the sweep has only its age backstop, and an intermediate lingers for weeks.
    /// <para>
    /// One statement over the tree's rows rather than a row of its own per execution. The alternative
    /// was a table of endings joined at sweep time, which is a second table growing with terminated
    /// executions and outliving the blobs it describes. This writes where the question is asked.
    /// </para>
    /// <para>
    /// A blob attached <em>after</em> the ending was recorded keeps a null ending, and so is treated
    /// as belonging to a tree that has not finished — conservative, which is the only safe direction,
    /// and unreachable in practice because attaching happens inside the run.
    /// </para>
    /// </summary>
    /// <returns>How many attachments were marked, which is zero for a tree that touched no blob.</returns>
    Task<int> RecordRunTreeEndedAsync(
        ExecutionId rootExecution,
        RunTreeOutcome outcome,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Blobs the daemon may now remove, oldest first, at most <paramref name="limit"/> of them.
    /// <para>
    /// A query rather than a predicate the caller applies, because the alternative is loading every
    /// blob the daemon has ever held to discard almost all of them. The rule is
    /// <see cref="BlobRetentionPolicy"/>'s and is documented there; that it is expressed twice — once
    /// as that record and once as this statement — is the cost of it being one round trip.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<StoredBlob>> FindReclaimableAsync(
        BlobRetentionPolicy policy,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every blob attached to one run tree, each with whether some <em>other</em> tree it belongs to
    /// has yet to end. <c>DeleteExecution</c> skips those rather than deleting them: a durable upload
    /// shared with a live run must survive the deletion of this one.
    /// </summary>
    Task<IReadOnlyList<RunTreeBlob>> FindRunTreeBlobsAsync(
        ExecutionId rootExecution, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets that a blob ever belonged to this run tree, for every blob that did. Called when the
    /// execution record itself goes, so that the join table does not accumulate rows naming
    /// executions nothing can look up. A blob attached to other trees keeps those.
    /// </summary>
    Task DetachRunTreeAsync(ExecutionId rootExecution, CancellationToken cancellationToken);
}

/// <summary>
/// A blob as <c>DeleteExecution</c> has to see it: the record, plus the one fact that decides whether
/// this call may touch it.
/// </summary>
/// <param name="ReachableElsewhere">
/// Another run tree this blob belongs to has not ended. The spec's word is "skipped": a blob a
/// non-terminal execution can still reference is not deleted, whatever the caller asked for.
/// </param>
public sealed record RunTreeBlob(StoredBlob Blob, bool ReachableElsewhere);
