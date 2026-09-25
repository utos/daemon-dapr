using Blobs.Domain;
using Common.Blobs;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Blobs.Application.Commands;

/// <summary>Remove one blob's bytes. <c>DeleteBlob</c>.</summary>
public sealed record DeleteBlobCommand(BlobId Id) : ICommand<BlobSummary>;

/// <summary>An id the daemon never issued. <c>NOT_FOUND</c> at the edge.</summary>
public sealed class BlobNotFoundException(BlobId id)
    : Exception($"No blob '{id}'.");

/// <summary>
/// A blob a live run could still read — <c>FAILED_PRECONDITION</c>.
/// <para>
/// A refusal rather than a deletion, because the two failures are not comparable: an operator who is
/// refused can see why and wait, while a run that fails because its bytes vanished mid-flight fails for
/// a reason its author cannot see anywhere in their document.
/// </para>
/// </summary>
public sealed class BlobHeldByALiveRunException(BlobId id)
    : Exception(
        $"Blob '{id}' is still reachable by an execution that has not finished. "
        + "Delete it once that run is terminal, or delete the execution with its blobs.");

/// <summary>
/// The bytes go and the record stays, which is the whole shape of removal here: <c>GetBlob</c> keeps
/// answering <c>DELETED</c> for an id it issued, so a handle in an old result reads as cleanup rather
/// than as corruption.
/// </summary>
public sealed class DeleteBlobHandler(
    IBlobRepository repository,
    ILogger<DeleteBlobHandler> logger,
    IObjectStore? store = null) : ICommandHandler<DeleteBlobCommand, BlobSummary>
{
    public async ValueTask<BlobSummary> Handle(DeleteBlobCommand command, CancellationToken ct)
    {
        var summary = await repository.SummaryAsync(command.Id, ct)
                      ?? throw new BlobNotFoundException(command.Id);

        // Idempotent on a blob already deleted, and asked before the precondition below: a blob whose
        // bytes are already gone cannot be harmed by a live run, so refusing here would make a retry
        // fail where the first call succeeded.
        if (summary.Blob.State == BlobState.Deleted) return summary;

        if (await repository.IsHeldByALiveRunAsync(command.Id, ct))
            throw new BlobHeldByALiveRunException(command.Id);

        var deleted = await RemoveAsync(summary.Blob, ct);

        logger.LogInformation("Deleted blob {BlobId} ({BlobBytes} bytes)", deleted.Id, deleted.Size);

        return summary with { Blob = deleted };
    }

    /// <summary>
    /// Bytes first, then the record — the order the sweep uses, and for the same reason. Interrupted
    /// this way round leaves a record saying <c>READY</c> for an object that is gone, which a later
    /// delete finishes because removing an object that is not there is a success. The other order
    /// leaves an object nothing remembers.
    /// </summary>
    private async Task<StoredBlob> RemoveAsync(StoredBlob blob, CancellationToken ct)
    {
        if (store is not null)
            await store.DeleteAsync(blob.Key, ct);

        var deleted = blob.Removed(DateTimeOffset.UtcNow);

        await repository.UpdateAsync(deleted, ct);

        return deleted;
    }
}
