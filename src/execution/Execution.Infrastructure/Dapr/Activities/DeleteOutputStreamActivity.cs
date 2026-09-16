using Common.SeedWork;
using Dapr.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record DeleteOutputStreamInput(ExecutionId ExecutionId);

/// <summary>
/// Drops a sub-workflow execution's output stream once its caller is done with it.
/// <para>
/// The caller is the only party that can do this while a run is still going, and a run that never
/// finishes is exactly when it matters: a poller looping forever with a sub-workflow call in the
/// loop body starts one child per lap, and nothing else would reclaim any of them until the run
/// terminates — which, for a poller, is never. Loops have no ceiling, so that growth is unbounded.
/// </para>
/// <para>
/// Safe the moment the call returns. The child's result is replayed from orchestration history
/// rather than re-read from here, and the stream was never addressable from outside — a
/// <c>workflow.call</c> child has no execution record, so <c>WatchOutput</c> answers NOT_FOUND
/// for its id.
/// </para>
/// </summary>
public class DeleteOutputStreamActivity(IEventStoreRepository eventStore)
    : WorkflowActivity<DeleteOutputStreamInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, DeleteOutputStreamInput input)
    {
        await eventStore.DeleteStreamAsync(StreamNames.ExecutionOutput(input.ExecutionId));

        return new object();
    }
}
