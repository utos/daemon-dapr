using Execution.Domain.Constants;

namespace Execution.Domain.Interpreter;

/// <summary>
/// Decides what happens at an activity name, before anything runs: reserved keywords, the
/// activity lookup, loop detection and the loop budget, and whether the segment should be
/// restarted.
///
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
        if (string.Equals(activityName, ReservedKeywords.End, StringComparison.OrdinalIgnoreCase))
            return new WorkflowStep.Complete();

        if (string.Equals(activityName, ReservedKeywords.Error, StringComparison.OrdinalIgnoreCase))
            return new WorkflowStep.Fail("Workflow transitioned to 'error'");

        // Checked here rather than after the previous transition so the reserved names above
        // are already excluded: an execution never continues as new just to reach 'end'.
        if (cursor.ShouldContinueAsNew(limits))
            return new WorkflowStep.ContinueAsNew(cursor);

        if (!graph.TryGetActivity(activityName, out var activity))
            return new WorkflowStep.Fail(
                $"Activity '{activityName}' not found in workflow '{graph.WorkflowKey}'");

        var entry = cursor.Enter(activityName, limits);

        if (entry.LimitExceeded)
            return new WorkflowStep.Fail(
                $"Workflow exceeded the maximum of {limits.MaxLoopedTransitions} looped activity " +
                $"executions; the loop re-enters activity '{activityName}'. Either the " +
                $"loop has no reachable exit, or the limit is too low for this workflow.");

        return new WorkflowStep.Run(activityName, activity, entry.Cursor!, entry.LoopJustDetected);
    }
}
