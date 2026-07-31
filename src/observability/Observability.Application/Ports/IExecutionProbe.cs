using Common.SeedWork;

namespace Observability.Application.Ports;

/// <summary>
/// Answers whether an execution exists at all, so a watch on an unknown id fails fast instead of
/// hanging on a stream that will never produce anything. Deliberately narrower than a repository:
/// the observability side has no business knowing how executions are stored or named.
/// </summary>
public interface IExecutionProbe
{
    Task<bool> ExistsAsync(ExecutionId executionId, CancellationToken ct);
}
