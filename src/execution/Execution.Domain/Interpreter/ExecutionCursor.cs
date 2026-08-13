namespace Execution.Domain.Interpreter;

/// <summary>
/// Where a single execution path has been. Immutable, so the driver can carry it across a
/// continue-as-new boundary and replay lands on the same value every time.
/// </summary>
/// <param name="Visited">
/// Activities already executed on this path. The interpreter walks a single path — promise
/// branches and sub-workflows are separate child executions — so reaching an activity twice
/// can only mean a back-edge.
/// </param>
/// <param name="TransitionsThisSegment">
/// Activities entered since this orchestration instance started, reset by a continuation.
/// </param>
/// <param name="LoopDetected">
/// A back-edge has been taken. Carried across continuations because it is what makes a segment
/// eligible to continue as new at all, and a continuation would otherwise look like a fresh run
/// walking a linear path.
/// </param>
public sealed record ExecutionCursor(
    IReadOnlySet<string> Visited,
    int TransitionsThisSegment,
    bool LoopDetected)
{
    /// <summary>The cursor an execution segment begins with.</summary>
    public static ExecutionCursor Start(IEnumerable<string>? visited, bool loopDetected)
    {
        // Ordinal to match how Spec.Activities (a protobuf MapField) is keyed.
        var set = new HashSet<string>(visited ?? [], StringComparer.Ordinal);

        return new ExecutionCursor(set, 0, loopDetected);
    }

    /// <summary>
    /// Records entering <paramref name="activityName"/>, detecting a back-edge the first time
    /// one is taken.
    /// </summary>
    public CursorEntry Enter(string activityName)
    {
        var visited = new HashSet<string>(Visited, StringComparer.Ordinal);
        var loopDetected = LoopDetected;
        var loopJustDetected = false;

        if (!visited.Add(activityName) && !loopDetected)
        {
            loopDetected = true;
            loopJustDetected = true;
        }

        return new CursorEntry(
            new ExecutionCursor(visited, TransitionsThisSegment + 1, loopDetected),
            loopJustDetected);
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

/// <param name="LoopJustDetected">
/// This entry is the one that discovered the loop, so the driver reports it once rather than on
/// every lap.
/// </param>
public sealed record CursorEntry(ExecutionCursor Cursor, bool LoopJustDetected);
