using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionScheduledEvent(Guid ExecutionId, DateTime DateOccurred) : IDomainEvent;
