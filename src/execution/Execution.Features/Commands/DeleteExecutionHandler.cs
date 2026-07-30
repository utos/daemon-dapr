using Common.SeedWork;
using Execution.Features.Exceptions;
using Execution.Features.Repositories;
using Mediator;

namespace Execution.Features.Commands;

/// <summary>
/// Deletes a terminal execution and its non-detached children across both stores.
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

        // Discover children before deleting anything — the read-model rows are how we find them.
        var childIds = await repository.GetChildExecutionIdsAsync(command.ExecutionId, ct);
        var allIds = new List<Guid>(childIds.Count + 1) { command.ExecutionId };
        allIds.AddRange(childIds);

        // Streams first (see class remarks). SubWorkflowCalls stream may not exist — delete is idempotent.
        foreach (var id in allIds)
        {
            await eventStore.DeleteStreamAsync(StreamNames.Execution(id), ct);
            await eventStore.DeleteStreamAsync(StreamNames.SubWorkflowCalls(id), ct);
        }

        await repository.DeleteAsync(allIds, ct);

        return new DeleteExecutionResult();
    }
}
