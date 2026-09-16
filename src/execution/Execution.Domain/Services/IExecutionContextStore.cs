using Common.SeedWork;
namespace Execution.Domain.Services;

public interface IExecutionContextStore
{
    Task SaveContextAsync(ExecutionContext context, CancellationToken ct = default);
    Task<ExecutionContext> LoadContextAsync(ExecutionId executionId, CancellationToken ct = default);

    /// <summary>
    /// Every sub-workflow execution started under <paramref name="rootExecutionId"/>, at any
    /// depth. The only way to find them: a <c>workflow.call</c> child has no execution record of
    /// its own, so its context row is the sole evidence it ran.
    /// </summary>
    Task<IReadOnlyList<ExecutionId>> ListDescendantsAsync(
        ExecutionId rootExecutionId, CancellationToken ct = default);

    /// <summary>
    /// Drops the run's context and every descendant's. Callers that need anything else keyed by
    /// those ids must take it <em>before</em> calling this — these rows are the only index to a
    /// sub-workflow execution, so removing them makes whatever is left unreachable.
    /// </summary>
    Task DeleteAllAsync(ExecutionId rootExecutionId, CancellationToken ct = default);
}
