using Common.SeedWork;

namespace Execution.Domain.Events;

public record SubWorkflowCreatedEvent(
    Guid ExecutionId,
    Guid ParentExecutionId,
    string WorkflowName,
    string WorkflowVersion,
    DateTime DateOccurred) : IDomainEvent;
