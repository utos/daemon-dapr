using Common.SeedWork;

namespace Execution.Domain.Events;

public record WorkflowExecutionActiveEvent(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent;
