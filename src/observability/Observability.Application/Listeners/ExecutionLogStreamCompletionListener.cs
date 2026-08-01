using Common.ExecutionLifecycle;
using Common.SeedWork;
using Observability.Domain;

namespace Observability.Application.Listeners;

/// <summary>
/// Closes an execution's log stream when the run ends, emitting the terminal frame that tells a
/// watching client how it ended. A use case rather than plumbing: it stopped reading another
/// context's events to find out what happened, and is now simply told.
/// </summary>
public class ExecutionLogStreamCompletionListener(IExecutionLogStore store) : IExecutionLifecycleListener
{
    public Task OnCompletedAsync(ExecutionId executionId, DateTime occurredAt, CancellationToken ct = default)
    {
        store.CompleteWith(executionId, occurredAt, ExecutionOutcome.Completed, null);
        return Task.CompletedTask;
    }

    public Task OnFailedAsync(ExecutionId executionId, DateTime occurredAt, string reason, CancellationToken ct = default)
    {
        store.CompleteWith(executionId, occurredAt, ExecutionOutcome.Failed, reason);
        return Task.CompletedTask;
    }
}
