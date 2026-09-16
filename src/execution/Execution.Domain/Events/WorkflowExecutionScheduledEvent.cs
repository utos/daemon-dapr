using Common.SeedWork;

namespace Execution.Domain.Events;

[EventType("utos.execution.scheduled")]
public record WorkflowExecutionScheduledEvent(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent;
