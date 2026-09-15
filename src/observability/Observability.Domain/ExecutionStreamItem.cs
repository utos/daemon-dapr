namespace Observability.Domain;

/// <summary>
/// An item flowing over an execution's watch stream: either an ordinary log line
/// (<see cref="LogItem"/>) or the single terminal frame (<see cref="TerminalItem"/>)
/// emitted when the execution reaches a completed/failed state, just before the stream closes.
/// </summary>
public abstract record ExecutionStreamItem;

public sealed record LogItem(ExecutionLogEntry Entry) : ExecutionStreamItem;

/// <param name="FailureCode">
/// An authored <c>error</c> action's code; null for a failure the daemon raised, which the frame
/// reports under the generic code.
/// </param>
public sealed record TerminalItem(
    DateTime Timestamp, ExecutionOutcome Outcome, string? FailureReason, string? FailureCode = null)
    : ExecutionStreamItem;

public enum ExecutionOutcome
{
    Completed,
    Failed
}
