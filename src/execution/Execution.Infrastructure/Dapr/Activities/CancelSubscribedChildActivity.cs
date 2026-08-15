using Common.SeedWork;
using Dapr.Workflow;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record CancelSubscribedChildInput(ExecutionId ChildExecutionId);

/// <summary>
/// Stops a sub-workflow whose caller is no longer consuming what it produces.
/// <para>
/// A gated producer spends most of its life parked on an acknowledgement. When its caller ends —
/// whether by reaching a terminal state or by an <c>on_emitted</c> handler leaving for somewhere
/// other than the call activity — that acknowledgement is never coming, and nothing else would
/// ever stop it. Termination is idempotent and treats a missing instance as an ordinary outcome,
/// so abandoning a producer that already finished on its own is not an error.
/// </para>
/// </summary>
public class CancelSubscribedChildActivity(IExecutionRuntime runtime)
    : WorkflowActivity<CancelSubscribedChildInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, CancelSubscribedChildInput input)
    {
        await runtime.TerminateAsync(input.ChildExecutionId);

        return new object();
    }
}
