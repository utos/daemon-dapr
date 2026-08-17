namespace Execution.Domain.Interpreter;

/// <summary>Where an activity's outcome sends the execution next.</summary>
public abstract record TransitionDecision
{
    private TransitionDecision()
    {
    }

    /// <summary>Continue at <paramref name="ActivityName"/> with <paramref name="Input"/>.</summary>
    public sealed record Goto(string ActivityName, Dictionary<string, object>? Input) : TransitionDecision;

    /// <summary>End this path, producing <paramref name="Value"/> as its result.</summary>
    public sealed record Return(object? Value) : TransitionDecision;

    /// <summary>
    /// Append <paramref name="Value"/> to this execution's output stream, then continue at
    /// <paramref name="ActivityName"/> with <paramref name="Input"/>. Emit-and-continue, as
    /// against <see cref="Return"/>'s emit-and-terminate.
    /// </summary>
    public sealed record Emit(
        Dictionary<string, object> Value,
        string ActivityName,
        Dictionary<string, object>? Input) : TransitionDecision;

    /// <summary>End this path with no result of its own.</summary>
    public sealed record End : TransitionDecision;

    /// <summary>
    /// The failure that was being handled is not handled: no <c>on_failure</c> rule matched, so it
    /// keeps travelling outward.
    ///
    /// Distinct from <see cref="End"/>, and the distinction is the whole point. On the success path
    /// "no rule matched" means the run finished; on the failure path the spec requires the opposite
    /// — "If no transitions match or list is empty, the error propagates"
    /// (<c>activity.proto</c>). Collapsing the two would report a failed run as Completed, which is
    /// the kind of wrong that no test notices unless it is looking for it.
    /// </summary>
    public sealed record Propagate : TransitionDecision;
}
