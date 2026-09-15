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

    // There is no `Complete` step. A path ending is the driver's loop finding no next activity,
    // decided by a rule (a `result` action, or the rules running out) — never by a name reaching
    // the navigator, since spec 0.0.16 has no `end` keyword for it to recognise.

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
