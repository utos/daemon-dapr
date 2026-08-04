using Common.SeedWork;

namespace Execution.Domain.Events;

[EventType("utos.execution.active")]
public record WorkflowExecutionActiveEvent(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent;
