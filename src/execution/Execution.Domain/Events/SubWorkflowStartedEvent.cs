using Common.SeedWork;

namespace Execution.Domain.Events;

public record SubWorkflowStartedEvent(
    Guid ParentExecutionId,
    Guid ChildExecutionId,
    string ActivityName,
    string WorkflowRef,
    string StartActivity,
    DateTime DateOccurred) : IDomainEvent;
