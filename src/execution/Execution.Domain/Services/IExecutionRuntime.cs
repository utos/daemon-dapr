using Common.SeedWork;

namespace Execution.Domain.Services;

/// <summary>
/// The durable orchestration behind an execution, as far as the rest of the daemon needs to know
/// about it. Keeps the workflow engine out of the application layer, the way
/// <see cref="IExecutionContextStore"/> keeps the database out of it.
/// </summary>
public interface IExecutionRuntime
{
    /// <summary>
    /// Tears down the orchestration for <paramref name="executionId"/>.
    /// <para>
    /// Implementations must be idempotent: terminating an orchestration that has already ended,
    /// or that never started, is a no-op rather than an error. Cancellation is driven by an
    /// at-least-once command path, so a duplicate must not turn into a failure.
    /// </para>
    /// <para>
    /// Sub-workflows a run is awaiting go down with it — they are children of this orchestration,
    /// and their results can no longer be observed. Executions started with
    /// <c>workflow.spawn</c> are independent and must not be touched.
    /// </para>
    /// </summary>
    Task TerminateAsync(ExecutionId executionId, CancellationToken cancellationToken = default);
}
