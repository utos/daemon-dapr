namespace Execution.Domain.Interpreter;

/// <summary>
/// Where a single execution path has been and how much looping it has done. Immutable, so
/// the driver can carry it across a continue-as-new boundary and replay lands on the same
/// value every time.
/// </summary>
/// <param name="Visited">
/// Activities already executed on this path. The interpreter walks a single path — promise
/// branches and sub-workflows are separate child executions — so reaching an activity twice
/// can only mean a back-edge.
/// </param>
/// <param name="LoopedTransitions">
/// Activities re-executed since a loop was entered, carried across continuations so the
/// ceiling survives them.
/// </param>
/// <param name="TransitionsThisSegment">
/// Activities entered since this orchestration instance started, reset by a continuation.
/// </param>
public sealed record ExecutionCursor(
    IReadOnlySet<string> Visited,
    int LoopedTransitions,
    int TransitionsThisSegment,
    bool LoopDetected)
{
    /// <summary>
    /// The cursor an execution segment begins with. A non-null <paramref name="visited"/>
    /// means this segment resumed via continue-as-new rather than being scheduled fresh.
    /// </summary>
    public static ExecutionCursor Start(IEnumerable<string>? visited, int loopedTransitions)
    {
        // Ordinal to match how Spec.Activities (a protobuf MapField) is keyed.
        var set = new HashSet<string>(visited ?? [], StringComparer.Ordinal);

        return new ExecutionCursor(set, loopedTransitions, 0, visited != null && loopedTransitions > 0);
    }

    /// <summary>
    /// Records entering <paramref name="activityName"/>, detecting a back-edge and charging
    /// it against the loop budget.
    /// </summary>
    public CursorEntry Enter(string activityName, ExecutionLimits limits)
    {
        var visited = new HashSet<string>(Visited, StringComparer.Ordinal);
        var looped = LoopedTransitions;
        var loopDetected = LoopDetected;
        var loopJustDetected = false;

        if (!visited.Add(activityName))
        {
            looped++;

            if (!loopDetected)
            {
                loopDetected = true;
                loopJustDetected = true;
            }

            // A ceiling of zero or less is no ceiling: an endless loop is legal, and
            // CancelExecution rather than a counter is what stops one.
            if (limits.MaxLoopedTransitions > 0 && looped >= limits.MaxLoopedTransitions)
                return CursorEntry.Exceeded;
        }

        var cursor = new ExecutionCursor(visited, looped, TransitionsThisSegment + 1, loopDetected);

        return new CursorEntry(cursor, loopJustDetected, false);
    }

    /// <summary>
    /// Whether this segment has done enough work to be worth restarting. Only once looping:
    /// continuing as new discards history, which is destructive for a long non-looping run
    /// someone may be debugging.
    /// </summary>
    public bool ShouldContinueAsNew(ExecutionLimits limits)
    {
        return LoopDetected && TransitionsThisSegment >= limits.ContinueAsNewAfterTransitions;
    }
}

/// <param name="LimitExceeded">
/// The loop budget ran out. <paramref name="Cursor"/> is not advanced in that case.
/// </param>
public sealed record CursorEntry(ExecutionCursor? Cursor, bool LoopJustDetected, bool LimitExceeded)
{
    public static readonly CursorEntry Exceeded = new(null, true, true);
}
