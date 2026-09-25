using Blobs.Domain;
using Common.Blobs;
using Mediator;
using Microsoft.Extensions.Options;

namespace Blobs.Application.Queries;

/// <summary>
/// One blob's metadata, and on request a location to download it from.
/// </summary>
/// <param name="IncludeDownload">
/// Also sign a download location, and record that a client was issued one. Ignored unless the blob is
/// <c>READY</c>: there is nothing to sign for an upload that never finished or bytes that are gone.
/// </param>
public sealed record GetBlobQuery(BlobId Id, bool IncludeDownload) : IQuery<BlobDetails?>;

/// <param name="Handle">
/// The value to place in a run's input — the stored form, covering the whole object. Absent unless the
/// blob is <c>READY</c>. The daemon builds it, because a client must never construct a handle itself.
/// </param>
public sealed record BlobDetails(
    BlobSummary Summary,
    WorkflowBlobHandle? Handle,
    SignedLocation? Download);

/// <summary>
/// A stored blob as a value, in the shape the transport maps to <c>utos.workflow.v1.Blob</c>. Not
/// <c>Common.Workflow.WorkflowBlob</c>: that type is what travels <em>through</em> a run, and what this
/// answers is a fresh handle over a whole object, which has no offset and no bytes to carry.
/// </summary>
public sealed record WorkflowBlobHandle(
    string Id, long Size, string MediaType, string? Name, DateTimeOffset? LastModified);

public sealed class GetBlobHandler(
    IBlobRepository repository,
    IOptions<BlobOptions> options,
    IObjectStore? store = null) : IQueryHandler<GetBlobQuery, BlobDetails?>
{
    public async ValueTask<BlobDetails?> Handle(GetBlobQuery query, CancellationToken ct)
    {
        var summary = await repository.SummaryAsync(query.Id, ct);

        // Null is NOT_FOUND at the edge, and only for an id the daemon never issued. A *deleted* blob
        // is still answered — as DELETED — so that a handle in an old result reads as removed rather
        // than as corrupt.
        if (summary is null) return null;

        var blob = summary.Blob;

        if (!blob.IsReadable)
            return new BlobDetails(summary, null, null);

        var handle = new WorkflowBlobHandle(
            blob.Id.Value, blob.Size, blob.MediaType, blob.Name, blob.LastModified);

        if (!query.IncludeDownload || store is null)
            return new BlobDetails(summary, handle, null);

        var download = await store.SignDownloadAsync(
            blob.Key, range: null, options.Value.LocationExpiry, ct);

        // Recorded because the spec counts it as a use: an operator asking why a blob is still around
        // should see that somebody fetched it. One row for every download, by aggregation — the event
        // involves no execution, so there is nothing to key a second row by.
        await repository.RecordEventAsync(
            query.Id, BlobEventKind.Downloaded, DateTimeOffset.UtcNow, usage: null, ct);

        return new BlobDetails(summary, handle, download);
    }
}
