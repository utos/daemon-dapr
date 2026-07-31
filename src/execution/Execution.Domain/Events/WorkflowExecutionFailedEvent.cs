using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionFailedEvent(ExecutionId ExecutionId, string Reason, DateTime DateOccurred) : IDomainEvent;
