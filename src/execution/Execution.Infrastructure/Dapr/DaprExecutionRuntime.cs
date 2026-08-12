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
    public async Task TerminateAsync(ExecutionId executionId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Dapr terminates child orchestrations along with their parent, which is exactly the
            // cascade the spec asks for: an awaited sub-workflow is a child of this instance,
            // while a spawned one is a top-level instance of its own and is untouched.
            await workflowClient.TerminateWorkflowAsync(executionId.ToString());
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
