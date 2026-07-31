using Common.SeedWork;

namespace Execution.Application.Exceptions;

/// <summary>
/// Thrown when an operation requires a terminal execution but the target is still
/// SCHEDULED or ACTIVE. Mapped to gRPC <c>FAILED_PRECONDITION</c> at the service boundary.
/// </summary>
public class ExecutionNotTerminalException(ExecutionId executionId)
    : Exception($"Execution '{executionId}' is not terminal and cannot be deleted; " +
                "it must be cancelled first")
{
    public ExecutionId ExecutionId { get; } = executionId;
}
