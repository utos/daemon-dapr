using Common.SeedWork;
using Dapr.Workflow;
using Execution.Application.Services;
using Execution.Domain.Aggregates;
using Execution.Infrastructure.Persistence;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record WriteLifecycleEventInput(
    ExecutionId ExecutionId,
    ExecutionLifecycleTransition Transition,
    string? FailureReason);

/// <summary>
/// Durable boundary around a lifecycle transition. Whether the transition applies at all is
/// the aggregate's decision; this exists so the write happens in an activity rather than the
/// orchestrator, and so a terminal transition drops the execution context in the same step.
/// </summary>
public class WriteLifecycleEventActivity(
    ExecutionLifecycleWriter lifecycle,
    ExecutionSubtreeReclaimer reclaimer)
    : WorkflowActivity<WriteLifecycleEventInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, WriteLifecycleEventInput input)
    {
        Action<WorkflowExecution> transition = input.Transition switch
        {
            ExecutionLifecycleTransition.Active => execution => execution.MarkActive(),
            ExecutionLifecycleTransition.Completed => execution => execution.Complete(),
            ExecutionLifecycleTransition.Failed => execution => execution.Fail(input.FailureReason),
            ExecutionLifecycleTransition.Cancelled => execution => execution.Cancel(input.FailureReason),
            _ => throw new ArgumentOutOfRangeException(
                nameof(input), input.Transition, "Unknown execution lifecycle transition")
        };

        await lifecycle.ApplyAsync(input.ExecutionId, transition);

        // A terminal run can no longer reach its sub-workflows, so their output streams and
        // context rows go with it. The backstop for the delete the driver performs as each call
        // returns: a run that failed part-way through never got there for the child it failed on.
        if (input.Transition is ExecutionLifecycleTransition.Completed
            or ExecutionLifecycleTransition.Failed
            or ExecutionLifecycleTransition.Cancelled)
        {
            await reclaimer.ReclaimAsync(input.ExecutionId);
        }

        return new object();
    }
}
