using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionCompletedEvent(Guid ExecutionId, DateTime DateOccurred) : IDomainEvent;
