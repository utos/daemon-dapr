using Common.Workflow;

namespace Execution.Domain.Interpreter;

/// <summary>
/// What the driver must do next. Failures are returned rather than thrown so the navigator
/// stays a total function over its inputs and every failure message can be asserted without
/// catching; turning one into an exception is the driver's job.
/// </summary>
public abstract record WorkflowStep
{
    private WorkflowStep()
    {
    }

    /// <summary>The path reached 'end'. Whatever result was already produced stands.</summary>
    public sealed record Complete : WorkflowStep;

    /// <summary>The execution cannot continue.</summary>
    public sealed record Fail(string Reason) : WorkflowStep;

    /// <summary>
    /// Restart the orchestration instance carrying <paramref name="Cursor"/> forward, to
    /// drop the instance history a loop would otherwise grow without bound.
    /// </summary>
    public sealed record ContinueAsNew(ExecutionCursor Cursor) : WorkflowStep;

    /// <summary>Run this activity, then advance with its output.</summary>
    public sealed record Run(
        string ActivityName,
        WorkflowActivity Activity,
        ExecutionCursor Cursor,
        bool LoopJustDetected) : WorkflowStep;
}
