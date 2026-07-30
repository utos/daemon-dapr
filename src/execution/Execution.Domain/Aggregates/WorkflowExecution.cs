using Common.SeedWork;
using Utos.Daemon.V1;
using Utos.Workflow.V1;
using Execution.Domain.Events;

namespace Execution.Domain.Aggregates;

public class WorkflowExecution : AggregateRoot
{
    public ExecutionStatus Status { get; private set; }

    public WorkflowExecution()
    {
    }

    public WorkflowExecution(WorkflowBundle bundle, Dictionary<string, object> input, string startActivity,
        Dictionary<string, string>? env = null, Guid? executionId = null)
    {
        // A detached sub-workflow supplies its own replay-safe id so re-scheduling is idempotent;
        // the gRPC path passes null and gets a fresh one.
        var id = executionId ?? Guid.NewGuid();

        ApplyChange(new WorkflowExecutionCreatedEvent(
            id, bundle, input, startActivity, DateTime.UtcNow, env));
    }

    public void Schedule()
    {
        ApplyChange(new WorkflowExecutionScheduledEvent(Id, DateTime.UtcNow));
    }

    protected override void When(IDomainEvent @event)
    {
        switch (@event)
        {
            case WorkflowExecutionCreatedEvent e:
                Handle(e);
                break;
            case WorkflowExecutionScheduledEvent e:
                Handle(e);
                break;
            // Lifecycle events (Active, Completed, Failed) are written directly to the stream
            // by WriteLifecycleEventActivity — no aggregate handling needed.
            case WorkflowExecutionActiveEvent:
            case WorkflowExecutionCompletedEvent:
            case WorkflowExecutionFailedEvent:
                break;
        }
    }

    private void Handle(WorkflowExecutionCreatedEvent @event)
    {
        Id = @event.ExecutionId;
        CreatedAt = @event.DateOccurred;

        Status = ExecutionStatus.Unspecified;
    }

    private void Handle(WorkflowExecutionScheduledEvent _)
    {
        if (Status != ExecutionStatus.Unspecified)
            throw new DomainException(
                $"Cannot schedule workflow execution. Expected status 'Unspecified', but current status is '{Status}'.");

        Status = ExecutionStatus.Scheduled;
    }
}
