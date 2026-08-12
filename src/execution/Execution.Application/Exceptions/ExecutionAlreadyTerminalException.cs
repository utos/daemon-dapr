using Common.SeedWork;

namespace Execution.Application.Exceptions;

/// <summary>
/// Thrown when an operation requires a running execution but the target already finished.
/// Mapped to gRPC <c>FAILED_PRECONDITION</c> at the service boundary.
/// <para>
/// The inverse of <see cref="ExecutionNotTerminalException"/>. Cancelling an already-cancelled
/// execution is *not* this: that is idempotent and succeeds. This is for a run that reached a
/// different terminal state under the caller's feet — the first terminal state wins, so the
/// honest answer is "it finished before you got here" rather than a silent success that implies
/// the cancel did something.
/// </para>
/// </summary>
public class ExecutionAlreadyTerminalException(ExecutionId executionId, string status)
    : Exception($"Execution '{executionId}' has already finished with status '{status}' " +
                "and cannot be cancelled")
{
    public ExecutionId ExecutionId { get; } = executionId;

    public string Status { get; } = status;
}
