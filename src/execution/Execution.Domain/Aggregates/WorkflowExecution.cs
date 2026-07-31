using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Events;

namespace Execution.Domain.Aggregates;

/// <summary>
/// The lifecycle of one workflow execution, and the only thing permitted to write to its
/// stream.
///
/// Two rules shape this type:
///
/// Guards live in the command methods, never in <see cref="When"/>. <see cref="When"/> is also
/// the rehydration path, so a guard there would make a stream that already contains a
/// contradiction impossible to load — an aggregate that cannot replay its own history is
/// unrecoverable, and the history is whatever was written before the rules existed.
///
/// Transitions are idempotent rather than strict: a repeated or late one is ignored, and the
/// first terminal state wins. The durable orchestration that drives them is at-least-once, so
/// a transition can arrive twice through no fault of the caller. Throwing would turn a
/// harmless duplicate write into a failed execution, and a throw raised while handling a
/// failure would replace the real reason with this one.
/// </summary>
public class WorkflowExecution : AggregateRoot<ExecutionId>
{
    public ExecutionStatus Status { get; private set; }

    /// <summary>Why the execution failed. Null unless <see cref="Status"/> is Failed.</summary>
    public string? FailureReason { get; private set; }

    public bool IsTerminal => Status is ExecutionStatus.Completed or ExecutionStatus.Failed;

    public WorkflowExecution()
    {
    }

    public WorkflowExecution(WorkflowBundle bundle, Dictionary<string, object> input, string startActivity,
        Dictionary<string, string>? env = null, ExecutionId? executionId = null, string? digest = null)
    {
        // A detached sub-workflow supplies its own replay-safe id so re-scheduling is idempotent;
        // the gRPC path passes null and gets a fresh one.
        var id = executionId ?? ExecutionId.New();

        ApplyChange(new WorkflowExecutionCreatedEvent(
            id, bundle, input, startActivity, DateTime.UtcNow, env, digest));
    }

    /// <summary>Accepted for execution. Ignored once the run has progressed past it.</summary>
    public void Schedule()
    {
        if (Status != ExecutionStatus.Unspecified)
            return;

        ApplyChange(new WorkflowExecutionScheduledEvent(Id, DateTime.UtcNow));
    }

    /// <summary>
    /// The orchestration has started running. Permitted from Unspecified as well as Scheduled,
    /// so a run whose Scheduled event never landed is not stuck short of Active.
    /// </summary>
    public void MarkActive()
    {
        if (Status is not (ExecutionStatus.Unspecified or ExecutionStatus.Scheduled))
            return;

        ApplyChange(new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow));
    }

    public void Complete()
    {
        if (IsTerminal)
            return;

        ApplyChange(new WorkflowExecutionCompletedEvent(Id, DateTime.UtcNow));
    }

    /// <param name="reason">
    /// Substituted when absent: the reason is surfaced to clients as the message of a
    /// WorkflowError, where a null would read as a bug in the daemon rather than a failure of
    /// the workflow.
    /// </param>
    public void Fail(string? reason)
    {
        if (IsTerminal)
            return;

        var described = string.IsNullOrWhiteSpace(reason)
            ? "Execution failed without a reported reason."
            : reason;

        ApplyChange(new WorkflowExecutionFailedEvent(Id, described, DateTime.UtcNow));
    }

    /// <summary>
    /// A plain fold, in stream order, with no guards. Last write wins — matching how the read
    /// model projects status — so a legacy stream containing a contradiction resolves the same
    /// way in both places. The command methods above are what stop a new stream from ever
    /// containing one.
    /// </summary>
    protected override void When(IDomainEvent @event)
    {
        switch (@event)
        {
            case WorkflowExecutionCreatedEvent e:
                Id = e.ExecutionId;
                CreatedAt = e.DateOccurred;
                Status = ExecutionStatus.Unspecified;
                break;

            case WorkflowExecutionScheduledEvent:
                Status = ExecutionStatus.Scheduled;
                break;

            case WorkflowExecutionActiveEvent:
                Status = ExecutionStatus.Active;
                break;

            case WorkflowExecutionCompletedEvent:
                Status = ExecutionStatus.Completed;
                break;

            case WorkflowExecutionFailedEvent e:
                Status = ExecutionStatus.Failed;
                FailureReason = e.Reason;
                break;
        }
    }
}
