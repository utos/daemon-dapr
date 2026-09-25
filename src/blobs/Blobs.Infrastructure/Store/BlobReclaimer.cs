using Blobs.Domain;
using Common.Blobs;
using Common.SeedWork;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blobs.Infrastructure.Store;

/// <summary>
/// <see cref="IBlobReclamation"/>: removing bytes nobody can reach any more.
/// <para>
/// Beside <see cref="BlobAccess"/> rather than in an application project, which is where a use case
/// would normally sit — this module has three projects and <see cref="BlobAccess"/> is already here.
/// A fourth project for two services, one of which would drag <c>BlobOptions</c> with it, would make
/// the module inconsistent with itself for no boundary gained: what the layering rules actually
/// forbid is the wire format reaching past <c>*.Api</c> and one module referencing another, and
/// neither is in question here.
/// </para>
/// </summary>
public sealed class BlobReclaimer(
    IBlobRepository repository,
    IOptions<BlobOptions> options,
    ILogger<BlobReclaimer> logger,
    IObjectStore? store = null) : IBlobReclamation
{
    public async Task<BlobReclamationReport> SweepAsync(CancellationToken cancellationToken)
    {
        // Nothing to delete bytes from, so nothing to sweep: without a store no blob was ever
        // stored, and an inline one lives and dies with the value that carries it.
        if (store is null) return BlobReclamationReport.Empty;

        var policy = options.Value.RetentionPolicy();
        var batch = Math.Max(1, options.Value.SweepBatchSize);
        var report = BlobReclamationReport.Empty;

        // Paged, and bounded. A first sweep after a long outage could have millions of candidates,
        // and a pass that tried to finish would hold a connection and a cancellation token for
        // hours; the next interval picks up where this one stopped.
        for (var pass = 0; pass < options.Value.SweepMaxBatches; pass++)
        {
            var candidates = await repository.FindReclaimableAsync(
                policy, DateTimeOffset.UtcNow, batch, cancellationToken);

            if (candidates.Count == 0) break;

            foreach (var candidate in candidates)
                report = report.Plus(await RemoveAsync(candidate, cancellationToken));

            if (candidates.Count < batch) break;
        }

        if (!report.DidNothing)
            logger.LogInformation(
                "Blob sweep removed {BlobsDeleted} blobs, skipped {BlobsSkipped}, failed {BlobsFailed}",
                report.Deleted, report.Skipped, report.Failed);

        return report;
    }

    public async Task<BlobReclamationReport> ReclaimRunTreeAsync(
        ExecutionId rootExecution, bool deleteDurable, CancellationToken cancellationToken)
    {
        var blobs = await repository.FindRunTreeBlobsAsync(rootExecution, cancellationToken);
        var report = BlobReclamationReport.Empty;

        foreach (var (blob, reachableElsewhere) in blobs)
            report = report.Plus(
                await ReclaimAsync(blob, reachableElsewhere, deleteDurable, cancellationToken));

        // Whatever was kept, the attachments naming this root are meaningless once the execution
        // record is gone, so they go too. A blob attached to other trees keeps those, and so keeps
        // being protected by them.
        await repository.DetachRunTreeAsync(rootExecution, cancellationToken);

        if (!report.DidNothing)
            logger.LogInformation(
                "Deleting execution {WorkflowExecutionId} removed {BlobsDeleted} blobs, "
                + "skipped {BlobsSkipped}, failed {BlobsFailed}",
                rootExecution, report.Deleted, report.Skipped, report.Failed);

        return report;
    }

    private async Task<BlobReclamationReport> ReclaimAsync(
        StoredBlob blob, bool reachableElsewhere, bool deleteDurable, CancellationToken cancellationToken)
    {
        // Already gone. Counted as skipped rather than deleted, because a caller asking what this
        // call did should not be told it removed bytes an earlier one removed.
        if (blob.State == BlobState.Deleted) return new BlobReclamationReport(0, 1, 0);

        // The spec's word is "skipped": a blob a non-terminal execution can still reference is not
        // deleted, whatever the caller asked for.
        if (reachableElsewhere) return new BlobReclamationReport(0, 1, 0);

        // An intermediate goes either way — nobody asked for it. A durable one goes only when asked,
        // and only if this run made it: a client's upload may have started other runs, and deleting
        // it belongs to `DeleteBlob`.
        var mayDelete = blob.Retention == BlobRetention.Intermediate
                        || (deleteDurable && blob.Origin == BlobOrigin.Produced);

        return mayDelete
            ? await RemoveAsync(blob, cancellationToken)
            : new BlobReclamationReport(0, 1, 0);
    }

    /// <summary>
    /// Bytes first, then the record, and the order is the whole of the recovery story. Interrupted
    /// this way round leaves a record that says <c>READY</c> for an object that is gone: a read fails
    /// <c>UTOS-F103</c>, and the next pass finds the same candidate and finishes the job, because
    /// deleting an object that is not there is a success. The other order leaves an object nothing
    /// remembers, which no pass can ever find.
    /// </summary>
    private async Task<BlobReclamationReport> RemoveAsync(
        StoredBlob blob, CancellationToken cancellationToken)
    {
        if (store is null) return new BlobReclamationReport(0, 1, 0);

        try
        {
            await store.DeleteAsync(blob.Key, cancellationToken);
        }
        catch (ObjectStoreException failure)
        {
            // Left readable on purpose. The record is the only thing that can find this object
            // again, so it is not touched until the bytes are certainly gone.
            logger.LogWarning(
                failure, "Could not delete the bytes of blob {BlobId}; leaving the record for the "
                + "next sweep", blob.Id);

            return new BlobReclamationReport(0, 0, 1);
        }

        await repository.UpdateAsync(blob.Removed(), cancellationToken);

        return new BlobReclamationReport(1, 0, 0);
    }
}
