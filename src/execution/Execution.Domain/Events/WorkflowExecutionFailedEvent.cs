using Common.SeedWork;

namespace Execution.Domain.Events;

[EventType("utos.execution.failed")]
public record WorkflowExecutionFailedEvent(ExecutionId ExecutionId, string Reason, DateTime DateOccurred) : IDomainEvent;
