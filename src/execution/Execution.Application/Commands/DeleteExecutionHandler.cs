using Common.Blobs;
using Common.SeedWork;
using Execution.Application.Exceptions;
using Execution.Application.Repositories;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Deletes a terminal execution across both stores.
///
/// Nothing cascades to another *execution*: an awaited sub-workflow is a Dapr child orchestration, not
/// an execution of its own — it has no event stream and no read-model row — and a detached one is an
/// independent top-level execution that deliberately outlives its parent.
///
/// It does cascade to the run tree's blobs, which are the one thing a deleted execution leaves behind
/// that costs money. Through the contract in <c>common/</c>, so this module still knows nothing about
/// blob records or where their bytes are.
///
/// Streams are deleted before read-model rows: if the process dies mid-delete, a leftover
/// read-model row can be re-deleted on retry, whereas a stream left behind after its row was
/// removed would be resurrected the next time the read model is rebuilt from the event log.
/// </summary>
public class DeleteExecutionHandler(
    IWorkflowExecutionRepository repository,
    IEventStoreRepository eventStore,
    IBlobReclamation blobs)
    : ICommandHandler<DeleteExecutionCommand, DeleteExecutionResult>
{
    public async ValueTask<DeleteExecutionResult> Handle(
        DeleteExecutionCommand command, CancellationToken ct)
    {
        var eligibility = await repository.GetDeletionEligibilityAsync(command.ExecutionId, ct);

        switch (eligibility)
        {
            case ExecutionDeletionEligibility.NotFound:
                throw new ExecutionNotFoundException(command.ExecutionId);
            case ExecutionDeletionEligibility.NonTerminal:
                throw new ExecutionNotTerminalException(command.ExecutionId);
        }

        // Streams first (see class remarks). An execution owns two: its lifecycle stream and the
        // output stream holding what it produced.
        await eventStore.DeleteStreamAsync(StreamNames.Execution(command.ExecutionId), ct);
        await eventStore.DeleteStreamAsync(StreamNames.ExecutionOutput(command.ExecutionId), ct);

        await repository.DeleteAsync([command.ExecutionId], ct);

        // Blobs last, and that order is deliberate. Reclaiming first would delete bytes a still-
        // existing execution's result names, if the delete below then failed; this way round a failure
        // leaves blobs whose tree has ended and whose attachment records say so, which is exactly
        // what the sweep collects. The one thing that does not self-heal is a durable blob this call
        // was asked to delete and could not, and `DeleteBlob` is how that is finished by hand.
        await blobs.ReclaimRunTreeAsync(command.ExecutionId, command.DeleteBlobs, ct);

        return new DeleteExecutionResult();
    }
}
