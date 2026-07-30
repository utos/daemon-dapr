namespace Execution.Features.Exceptions;

/// <summary>
/// Thrown when an operation targets an execution id that does not exist.
/// Mapped to gRPC <c>NOT_FOUND</c> at the service boundary.
/// </summary>
public class ExecutionNotFoundException(Guid executionId)
    : Exception($"Execution '{executionId}' not found")
{
    public Guid ExecutionId { get; } = executionId;
}
