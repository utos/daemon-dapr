using Common.SeedWork;
using Execution.Application.Exceptions;
using Execution.Domain.Aggregates;
using Execution.Domain.Services;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Execution.Application.Commands;

/// <summary>
/// Stops a running execution: tear the orchestration down, then record that it was cancelled.
///
/// That order matters. Terminating first means a failure to write the event leaves a dead
/// orchestration and a non-terminal record, which the next attempt repairs — termination is
/// idempotent, so a retry re-terminates harmlessly and writes. Recording first would invert it:
/// a failure to terminate would leave a run marked Cancelled while its orchestration kept
/// going, and for the case cancellation exists to serve — an intentionally endless poller —
/// nothing would ever come along to stop it.
///
/// The cascade to awaited sub-workflows is not implemented here because it does not need to be:
/// they are child orchestrations of this one, not executions of their own, so they go down with
/// their parent. A `workflow.spawn` child is a separate top-level orchestration and is
/// deliberately left running.
/// </summary>
public class CancelExecutionHandler(
    IEventStoreRepository eventStore,
    IExecutionRuntime runtime,
    ILogger<CancelExecutionHandler> logger)
    : ICommandHandler<CancelExecutionCommand, CancelExecutionResult>
{
    public async ValueTask<CancelExecutionResult> Handle(
        CancelExecutionCommand command, CancellationToken ct)
    {
        if (!await eventStore.ExistsAsync(StreamNames.Execution(command.ExecutionId), ct))
            throw new ExecutionNotFoundException(command.ExecutionId);

        var execution = await eventStore
            .LoadAsync<WorkflowExecution, ExecutionId>(command.ExecutionId, ct);

        // Already cancelled is a success: the caller asked for a state the run is in.
        if (execution.Status == ExecutionStatus.Cancelled)
        {
            logger.LogDebug(
                "Execution {WorkflowExecutionId} is already cancelled; nothing to do",
                command.ExecutionId);

            return new CancelExecutionResult();
        }

        // Any other terminal state wins, because it got there first.
        if (execution.IsTerminal)
            throw new ExecutionAlreadyTerminalException(command.ExecutionId, execution.Status.ToString());

        await runtime.TerminateAsync(command.ExecutionId, ct);

        execution.Cancel(command.Reason);

        // Nothing to save if the aggregate declined the transition — it reached a terminal state
        // between the guard above and here.
        if (execution.GetDomainEvents().Count > 0)
            await eventStore.SaveAsync<WorkflowExecution, ExecutionId>(execution);

        logger.LogInformation(
            "Cancelled execution {WorkflowExecutionId}{ReasonSuffix}",
            command.ExecutionId,
            command.Reason is null ? string.Empty : $": {command.Reason}");

        return new CancelExecutionResult();
    }
}
