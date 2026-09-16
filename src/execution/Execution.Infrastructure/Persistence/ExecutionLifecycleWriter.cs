using Common.SeedWork;
using Execution.Domain.Aggregates;
using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.Persistence;

/// <summary>
/// Applies a lifecycle transition to an execution durably: load, decide, append. The single
/// path by which anything writes to an execution's stream.
///
/// A conflict means another writer got there first, so the decision was made against stale
/// state. Retrying re-runs the transition against fresh state, where its own guard usually
/// turns it into a no-op — which is the correct outcome, not an error.
/// </summary>
public sealed class ExecutionLifecycleWriter(
    IEventStoreRepository eventStore,
    ILogger<ExecutionLifecycleWriter> logger)
{
    private const int MaxAttempts = 3;

    public async Task ApplyAsync(
        ExecutionId executionId,
        Action<WorkflowExecution> transition,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var execution = await eventStore.LoadAsync<WorkflowExecution, ExecutionId>(executionId, cancellationToken);

            transition(execution);

            if (execution.GetDomainEvents().Count == 0)
            {
                logger.LogDebug(
                    "Lifecycle transition left execution {WorkflowExecutionId} unchanged at status " +
                    "'{Status}' — already applied, or the run is already terminal",
                    executionId, execution.Status);
                return;
            }

            try
            {
                await eventStore.SaveAsync<WorkflowExecution, ExecutionId>(execution);
                return;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogDebug(
                    "Lost a concurrent write to execution {WorkflowExecutionId} on attempt {Attempt}; " +
                    "reloading to re-decide",
                    executionId, attempt);
            }
        }
    }
}
