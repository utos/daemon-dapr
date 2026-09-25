using Blobs.Domain;
using Common.Blobs;
using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blobs.Application.Commands;

/// <summary>
/// Remove every blob a filter matches, skipping those a live run can still reach. <c>PruneBlobs</c>.
/// </summary>
/// <param name="DryRun">Report what would go and remove nothing.</param>
public sealed record PruneBlobsCommand(BlobQuery Filter, bool DryRun) : ICommand<PruneResult>;

/// <param name="Skipped">
/// Matched and left alone, because a non-terminal execution can still reference them. Counted rather
/// than folded into the total, so an operator can tell "there was nothing to remove" from "there was,
/// and it is still in use".
/// </param>
public sealed record PruneResult(long Count, long Bytes, long Skipped);

/// <summary>An unfiltered prune. <c>INVALID_ARGUMENT</c> at the edge.</summary>
/// <remarks>
/// The spec requires at least one filter "so that an empty request cannot delete everything". A guard
/// against the request nobody means to send: <c>utos blob prune</c> with a flag mistyped should do
/// nothing, not empty the bucket.
/// </remarks>
public sealed class UnfilteredPruneException()
    : Exception(
        "A prune must name at least one of retention, origin, created_before or last_used_before. "
        + "An unfiltered prune would delete every blob the daemon holds.");

public sealed class PruneBlobsHandler(
    IBlobRepository repository,
    IOptions<BlobOptions> options,
    ILogger<PruneBlobsHandler> logger,
    IObjectStore? store = null) : ICommandHandler<PruneBlobsCommand, PruneResult>
{
    public async ValueTask<PruneResult> Handle(PruneBlobsCommand command, CancellationToken ct)
    {
        if (command.Filter.IsEmpty) throw new UnfilteredPruneException();

        // A blob already deleted has no bytes to remove and must not be counted twice across two
        // prunes, so the filter is narrowed to what is actually there. Callers cannot ask for this:
        // `PruneBlobsRequest` has no `state`, which is why it is decided here.
        var filter = command.Filter with { State = BlobState.Ready };

        long count = 0, bytes = 0, skipped = 0;
        var page = string.Empty;

        // Paged for the reason the sweep is: a first prune on a long-lived daemon has an unbounded
        // amount of work, and one transaction holding all of it would hold a connection for hours.
        while (true)
        {
            var batch = await repository.ListAsync(
                filter, options.Value.SweepBatchSize, page, ct);

            if (batch.Blobs.Count == 0) break;

            var live = await repository.HeldByALiveRunAsync(
                batch.Blobs.Select(found => found.Blob.Id).ToList(), ct);

            foreach (var found in batch.Blobs)
            {
                if (live.Contains(found.Blob.Id))
                {
                    skipped++;
                    continue;
                }

                count++;
                bytes += found.Blob.Size;

                if (!command.DryRun) await RemoveAsync(found.Blob, ct);
            }

            // A dry run has to page by token, since nothing it looked at changes and the same first
            // page would come back forever. A real prune could re-query from the start — the blobs it
            // removed no longer match `Ready` — but paging is correct for both and is one code path.
            if (string.IsNullOrEmpty(batch.NextPageToken)) break;

            page = batch.NextPageToken;
        }

        logger.LogInformation(
            "Prune {PruneMode} {BlobsDeleted} blobs ({BlobBytes} bytes), skipped {BlobsSkipped}",
            command.DryRun ? "would remove" : "removed", count, bytes, skipped);

        return new PruneResult(count, bytes, skipped);
    }

    private async Task RemoveAsync(StoredBlob blob, CancellationToken ct)
    {
        if (store is not null)
            await store.DeleteAsync(blob.Key, ct);

        await repository.UpdateAsync(blob.Removed(DateTimeOffset.UtcNow), ct);
    }
}
