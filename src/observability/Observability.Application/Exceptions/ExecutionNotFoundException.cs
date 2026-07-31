using Common.SeedWork;

namespace Observability.Application.Exceptions;

/// <summary>
/// Thrown when a watch targets an execution id that does not exist.
/// Mapped to gRPC <c>NOT_FOUND</c> at the service boundary.
/// </summary>
public class ExecutionNotFoundException(ExecutionId executionId)
    : Exception("Execution not found")
{
    public ExecutionId ExecutionId { get; } = executionId;
}
