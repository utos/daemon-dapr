using Common.SeedWork;
using Dapr.Workflow;
using Execution.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.Dapr;

/// <summary>
/// <see cref="IExecutionRuntime"/> over Dapr Workflows. The orchestration instance id is the
/// execution id, as set when the run was scheduled.
/// </summary>
public sealed class DaprExecutionRuntime(
    DaprWorkflowClient workflowClient,
    ILogger<DaprExecutionRuntime> logger) : IExecutionRuntime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Terminates the orchestration and does not return until it is observably gone.
    ///
    /// <para>
    /// The wait is the point. <c>TerminateWorkflowAsync</c> is documented to complete "when the
    /// termination has been enqueued", so on its own it says nothing about whether anything
    /// stopped — while everything the caller does next (writing the run's terminal output entry,
    /// deleting the execution context rows the orchestration's activities read on every step)
    /// assumes it did. Confirming a terminal status is what makes that assumption true.
    /// </para>
    ///
    /// <para>
    /// How long that may take is the caller's policy, not this method's, so the wait is bounded by
    /// the caller's token and by nothing else: a client that gives up cancels the wait with it,
    /// and a request that is abandoned no longer pins the daemon. Passing the token to Dapr is
    /// what makes that work — without it the call outlives the request that asked for it.
    /// </para>
    ///
    /// <para>
    /// What this cannot promise: Dapr lets in-flight activities run to completion after the
    /// instance is terminated, and a work item already queued can still be dispatched. Such an
    /// activity has nowhere to deliver its result and is discarded, but a caller must not read
    /// "terminal" as "nothing is touching this run's state any more".
    /// </para>
    /// </summary>
    public async Task TerminateAsync(ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        var instanceId = executionId.ToString();

        try
        {
            // Dapr terminates child orchestrations along with their parent, which is exactly the
            // cascade the spec asks for: an awaited sub-workflow is a child of this instance,
            // while a spawned one is a top-level instance of its own and is untouched.
            await workflowClient.TerminateWorkflowAsync(instanceId, cancellation: cancellationToken);
        }
        catch (Exception ex) when (IsAlreadyGone(ex))
        {
            // Nothing to tear down — the orchestration finished, or never started. Cancellation
            // arrives on an at-least-once path, so this is an ordinary outcome rather than a
            // failure; the caller still gets its record written.
            logger.LogDebug(
                ex,
                "No live orchestration to terminate for execution {WorkflowExecutionId}",
                executionId);

            return;
        }

        while (!await IsTerminalAsync(instanceId, cancellationToken))
            await Task.Delay(PollInterval, cancellationToken);
    }

    /// <summary>Whether the instance is gone or has stopped running.</summary>
    private async Task<bool> IsTerminalAsync(string instanceId, CancellationToken cancellationToken)
    {
        try
        {
            var state = await workflowClient.GetWorkflowStateAsync(instanceId, false, cancellationToken);

            // A missing instance reports Exists rather than throwing, so "never started" and
            // "already purged" both land here as terminal.
            return !state.Exists || state.IsWorkflowCompleted;
        }
        catch (Exception ex) when (IsAlreadyGone(ex))
        {
            return true;
        }
    }

    /// <summary>
    /// Matched on message rather than on a typed exception because the Dapr client surfaces a
    /// missing instance as a generic RPC fault; a typed catch would silently stop matching if the
    /// transport changed, and swallowing every exception would hide a real outage behind a
    /// "cancelled" that never happened.
    /// </summary>
    private static bool IsAlreadyGone(Exception ex) =>
        ex.Message.Contains("no such instance", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
