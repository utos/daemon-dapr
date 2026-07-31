using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionScheduledEvent(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent;
