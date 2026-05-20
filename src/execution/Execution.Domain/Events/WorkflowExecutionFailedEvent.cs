using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionFailedEvent(Guid ExecutionId, string Reason, DateTime DateOccurred) : IDomainEvent;
