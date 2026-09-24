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
}
