using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionActiveEvent(Guid ExecutionId, DateTime DateOccurred) : IDomainEvent;
