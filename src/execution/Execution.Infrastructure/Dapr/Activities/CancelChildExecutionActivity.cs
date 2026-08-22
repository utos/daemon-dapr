using Common.SeedWork;
using Dapr.Workflow;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record CancelChildExecutionInput(ExecutionId ChildExecutionId);

/// <summary>
/// Stops a sub-workflow whose caller will never observe it again.
/// <para>
/// Two callers, one reason. A gated producer spends most of its life parked on an
/// acknowledgement, and when its caller ends — by reaching a terminal state, or by an
/// <c>on_emitted</c> handler leaving for somewhere other than the call activity — that
/// acknowledgement is never coming. A promise branch is the same shape without the parking: once
/// the mode is satisfied, a losing branch's result can no longer be read, but the branch itself
/// carries on issuing requests and writing entries. Neither would ever be stopped by anything
/// else.
/// </para>
/// <para>
/// Termination is idempotent and treats a missing instance as an ordinary outcome, so cancelling
/// one that already finished on its own is not an error — which is what makes the race between a
/// promise settling and its branches finishing safe.
/// </para>
/// </summary>
public class CancelChildExecutionActivity(IExecutionRuntime runtime)
    : WorkflowActivity<CancelChildExecutionInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, CancelChildExecutionInput input)
    {
        await runtime.TerminateAsync(input.ChildExecutionId);

        return new object();
    }
}
