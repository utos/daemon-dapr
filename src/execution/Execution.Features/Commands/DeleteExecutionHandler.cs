using Common.SeedWork;
using Execution.Features.Exceptions;
using Execution.Features.Repositories;
using Mediator;

namespace Execution.Features.Commands;

/// <summary>
/// Deletes a terminal execution across both stores.
///
/// There is nothing to cascade to: an awaited sub-workflow is a Dapr child orchestration, not an
/// execution of its own — it has no event stream and no read-model row — and a detached one is an
/// independent top-level execution that deliberately outlives its parent.
///
/// Streams are deleted before read-model rows: if the process dies mid-delete, a leftover
/// read-model row can be re-deleted on retry, whereas a stream left behind after its row was
/// removed would be resurrected the next time the read model is rebuilt from the event log.
/// </summary>
public class DeleteExecutionHandler(
    IWorkflowExecutionRepository repository,
    IEventStoreRepository eventStore)
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

        // Stream first (see class remarks).
        await eventStore.DeleteStreamAsync(StreamNames.Execution(command.ExecutionId), ct);

        await repository.DeleteAsync([command.ExecutionId], ct);

        return new DeleteExecutionResult();
    }
}
