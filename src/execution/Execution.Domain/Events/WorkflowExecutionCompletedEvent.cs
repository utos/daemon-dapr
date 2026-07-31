using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionCompletedEvent(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent;
