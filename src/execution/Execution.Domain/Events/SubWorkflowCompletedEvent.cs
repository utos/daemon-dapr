using Common.SeedWork;

namespace Execution.Domain.Events;

public record SubWorkflowCompletedEvent(
    Guid ParentExecutionId,
    Guid ChildExecutionId,
    string ActivityName,
    DateTime DateOccurred) : IDomainEvent;
