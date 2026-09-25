using Common.SeedWork;

namespace Blobs.Domain;

/// <summary>
/// Which blobs to answer with — <c>ListBlobsRequest</c>, and the subset of it <c>PruneBlobs</c> takes.
/// <para>
/// Every field is optional and they combine with AND. A filter with nothing set means "all", which is
/// a reasonable listing and a catastrophic prune — so <c>PruneBlobs</c> refuses an empty one rather
/// than this type forbidding it, because the same type has to serve both.
/// </para>
/// </summary>
/// <param name="RootExecution">
/// Blobs one run tree produced or had attached. A join through the attachments rather than a column:
/// a blob belongs to any number of trees, which is the whole reason membership is a table.
/// </param>
/// <param name="LastUsedBefore">
/// Blobs whose newest history event is older than this, <em>or that have none at all</em>. The second
/// half is what makes this usable for the thing an operator wants it for — finding what nothing has
/// touched — and leaving it out would silently exempt every blob that was never used.
/// </param>
public sealed record BlobQuery(
    BlobRetention? Retention = null,
    BlobOrigin? Origin = null,
    BlobState? State = null,
    ExecutionId? RootExecution = null,
    DateTimeOffset? CreatedBefore = null,
    DateTimeOffset? LastUsedBefore = null)
{
    /// <summary>Whether this filter narrows anything at all. <c>PruneBlobs</c>'s precondition.</summary>
    public bool IsEmpty =>
        Retention is null && Origin is null && State is null
        && RootExecution is null && CreatedBefore is null && LastUsedBefore is null;
}

/// <summary>
/// A blob as <c>BlobInfo</c> needs it: the record, plus the two facts that are not on it because they
/// are aggregates of its history.
/// </summary>
/// <param name="LastUsedAt">
/// The newest event in the blob's timeline, or null when nothing has happened to it yet. A produced
/// blob has a <c>produced</c> row from the moment it exists, so null here means an upload that was
/// created and never used.
/// </param>
/// <param name="UsageCount">
/// How many distinct <c>(event, root execution, workflow)</c> rows attached or read it — the number of
/// history rows with those kinds, not the number of times bytes moved. The spec's wording, and the
/// reason it is a small number rather than an unbounded one.
/// </param>
public sealed record BlobSummary(StoredBlob Blob, DateTimeOffset? LastUsedAt, long UsageCount);

/// <summary>One page of a listing, and where the next one starts.</summary>
/// <param name="NextPageToken">
/// Empty on the last page. It is the id the next page continues after, and ids are time-ordered at the
/// front — so "newest first" is descending id, and a cursor is one comparison rather than an offset
/// that shifts when a blob is created mid-listing.
/// </param>
public sealed record BlobPage(IReadOnlyList<BlobSummary> Blobs, string NextPageToken);
