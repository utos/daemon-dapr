using Common.SeedWork;

namespace Execution.Domain.Events;

/// <summary>
/// The execution was stopped on purpose.
/// </summary>
/// <param name="Reason">
/// Why, when anyone said. Nullable rather than substituted the way a failure reason is: a
/// failure with no reason reads as a bug in the daemon and needs standing in for, whereas
/// "cancelled, no reason given" is an ordinary thing for a caller to mean.
/// </param>
[EventType("utos.execution.cancelled")]
public record WorkflowExecutionCancelledEvent(
    ExecutionId ExecutionId,
    string? Reason,
    DateTime DateOccurred) : IDomainEvent;
