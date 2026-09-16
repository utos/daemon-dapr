using Common.Workflow;

namespace Execution.Domain.Interpreter;

/// <summary>
/// Decides what happens at an activity name, before anything runs: the activity lookup, loop
/// detection and the loop budget, and whether the segment should be restarted.
/// <para>
/// A name is always an activity. Ending or failing a path is an action a rule takes — the
/// interpreter's <see cref="TransitionDecision.Return"/> and <see cref="TransitionDecision.Fail"/>
/// — and never a name the driver has to recognise, so there are no keywords here; there were,
/// until spec 0.0.15, and their case-insensitive match was the only thing in the daemon that was.
/// </para>
/// Static and dependency-free by design. The durable driver that calls this is instantiated
/// by the orchestration runtime and has no constructor injection, so anything it calls
/// inline has to be newable or static.
/// </summary>
public static class WorkflowNavigator
{
    public static WorkflowStep Next(
        WorkflowGraph graph,
        string activityName,
        ExecutionCursor cursor,
        ExecutionLimits limits)
    {
        // Before the lookup, so the execution restarts its segment at the activity it is about
        // to run rather than after it. An execution never continues as new just to end: a path
        // that ends is a null next activity, and the driver's loop exits before reaching here.
        if (cursor.ShouldContinueAsNew(limits))
            return new WorkflowStep.ContinueAsNew(cursor);

        if (!graph.TryGetActivity(activityName, out var activity))
            return new WorkflowStep.Fail(
                $"Activity '{activityName}' not found in workflow '{graph.WorkflowKey}'");

        // Nothing here caps how many times a loop may go round. An endless loop is legal, and
        // CancelExecution is what stops one; the ceiling that used to live here only existed
        // because nothing else could.
        var entry = cursor.Enter(activityName);

        return new WorkflowStep.Run(activityName, activity, entry.Cursor, entry.LoopJustDetected);
    }
}
