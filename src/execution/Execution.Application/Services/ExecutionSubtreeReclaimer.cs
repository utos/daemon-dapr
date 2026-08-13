using Common.SeedWork;
using Execution.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Execution.Application.Services;

/// <summary>
/// Reclaims what a run's sub-workflow executions leave behind, once the run can no longer use
/// them.
/// <para>
/// A <c>workflow.call</c> child is a child orchestration rather than an execution of its own: no
/// aggregate stream, no read-model row, and <c>WatchOutput</c> returns NOT_FOUND for its id. It
/// does have an output stream, which the consumer side reads, and a context row — and that row is
/// the only evidence anywhere that the child existed. Reclaim in the wrong order and the streams
/// become unreachable rather than deleted, so the streams go first and the index that finds them
/// goes last.
/// </para>
/// <para>
/// Called from both ends a run can stop at: the driver's terminal transition, and cancellation,
/// where the orchestration is torn down and the driver never gets to run at all.
/// </para>
/// </summary>
public sealed class ExecutionSubtreeReclaimer(
    IExecutionContextStore contextStore,
    IEventStoreRepository eventStore,
    ILogger<ExecutionSubtreeReclaimer> logger)
{
    public async Task ReclaimAsync(ExecutionId rootExecutionId, CancellationToken ct = default)
    {
        var descendants = await contextStore.ListDescendantsAsync(rootExecutionId, ct);

        // Streams before the index that names them (see class remarks). Deleting a stream is
        // idempotent, so a crash between the two is repaired by the next attempt rather than
        // leaving something stranded.
        foreach (var descendant in descendants)
            await eventStore.DeleteStreamAsync(StreamNames.ExecutionOutput(descendant), ct);

        if (descendants.Count > 0)
            logger.LogDebug(
                "Reclaimed {SubWorkflowCount} sub-workflow output streams under execution " +
                "{WorkflowExecutionId}",
                descendants.Count, rootExecutionId);

        await contextStore.DeleteAllAsync(rootExecutionId, ct);
    }
}
