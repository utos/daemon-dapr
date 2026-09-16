using Common.SeedWork;

namespace Execution.Domain.Events;

[EventType("utos.execution.completed")]
public record WorkflowExecutionCompletedEvent(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent;
