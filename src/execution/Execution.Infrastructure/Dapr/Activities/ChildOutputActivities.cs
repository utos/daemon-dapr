using Common.SeedWork;
using Dapr.Workflow;
using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record RaiseChildOutputInput(
    ExecutionId ConsumerExecutionId,
    string EventName,
    ChildOutputMessage Message);

/// <summary>
/// Hands one output message from a sub-workflow to the caller consuming it. An activity because
/// an orchestrator cannot do I/O, and recorded in history so a replay does not re-deliver.
/// </summary>
public class RaiseChildOutputActivity(
    DaprWorkflowClient workflowClient,
    ILogger<RaiseChildOutputActivity> logger)
    : WorkflowActivity<RaiseChildOutputInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, RaiseChildOutputInput input)
    {
        await DaprInstance.RaiseOrIgnoreGoneAsync(
            workflowClient, input.ConsumerExecutionId, input.EventName, input.Message, logger);

        return new object();
    }
}

public sealed record AckChildOutputInput(ExecutionId ChildExecutionId);

/// <summary>
/// Tells a gated child that the value it is waiting on has been consumed, which is what releases
/// it to produce the next one. The rendezvous carries no sequence: at depth one there is exactly
/// one unconsumed value, so "the one you are waiting on" is unambiguous.
/// </summary>
public class AckChildOutputActivity(
    DaprWorkflowClient workflowClient,
    ILogger<AckChildOutputActivity> logger)
    : WorkflowActivity<AckChildOutputInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, AckChildOutputInput input)
    {
        await DaprInstance.RaiseOrIgnoreGoneAsync(
            workflowClient, input.ChildExecutionId, OutputEvents.Consumed, true, logger);

        return new object();
    }
}

internal static class DaprInstance
{
    /// <summary>
    /// Raises an event, treating a missing instance as an ordinary outcome. Either end of this
    /// rendezvous can be gone by the time the other speaks — a cancelled caller, a child torn
    /// down with its parent — and an event addressed to a finished instance is nothing to fail a
    /// run over.
    /// </summary>
    public static async Task RaiseOrIgnoreGoneAsync(
        DaprWorkflowClient workflowClient,
        ExecutionId target,
        string eventName,
        object payload,
        ILogger logger)
    {
        try
        {
            await workflowClient.RaiseEventAsync(target.ToString(), eventName, payload);
        }
        catch (Exception ex) when (IsAlreadyGone(ex))
        {
            logger.LogDebug(
                ex,
                "No live orchestration for {WorkflowExecutionId} to receive '{EventName}'",
                target, eventName);
        }
    }

    /// <summary>
    /// Matched on message rather than a typed exception for the same reason
    /// <c>DaprExecutionRuntime</c> does: the client surfaces a missing instance as a generic RPC
    /// fault, and swallowing everything would hide a real outage.
    /// </summary>
    private static bool IsAlreadyGone(Exception ex) =>
        ex.Message.Contains("no such instance", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
