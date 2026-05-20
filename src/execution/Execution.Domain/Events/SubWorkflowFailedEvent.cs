using Common.SeedWork;

namespace Execution.Domain.Events;

public record SubWorkflowFailedEvent(
    Guid ParentExecutionId,
    Guid ChildExecutionId,
    string ActivityName,
    string Reason,
    DateTime DateOccurred) : IDomainEvent;
