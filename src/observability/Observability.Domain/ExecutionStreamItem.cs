namespace Observability.Domain;

/// <summary>
/// An item flowing over an execution's watch stream: either an ordinary log line
/// (<see cref="LogItem"/>) or the single terminal frame (<see cref="TerminalItem"/>)
/// emitted when the execution reaches a completed/failed state, just before the stream closes.
/// </summary>
public abstract record ExecutionStreamItem;

public sealed record LogItem(ExecutionLogEntry Entry) : ExecutionStreamItem;

public sealed record TerminalItem(DateTime Timestamp, ExecutionOutcome Outcome, string? FailureReason)
    : ExecutionStreamItem;

public enum ExecutionOutcome
{
    Completed,
    Failed
}
