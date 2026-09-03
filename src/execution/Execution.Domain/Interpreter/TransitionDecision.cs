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

    /// <summary>
    /// Run <paramref name="Workflow"/> from <paramref name="StartActivity"/> as a child execution,
    /// then come back for the next emitted value.
    ///
    /// One of the two answers on the <c>Emitted</c> path that keep consuming. Distinct from
    /// <see cref="Goto"/> because it leaves this workflow: nothing the dispatched document does can
    /// land in this execution's flow, which is what stops "ran out of transitions" meaning one
    /// thing inside a handler and another outside it.
    /// </summary>
    public sealed record Dispatch(
        string Workflow,
        string StartActivity,
        Dictionary<string, object>? Input) : TransitionDecision;

    /// <summary>
    /// Back to <paramref name="ActivityName"/> for the next emitted value, on the same
    /// subscription.
    ///
    /// Identical to <see cref="Goto"/> in where it sends the run and deliberately not the same
    /// case, because on the emitted path the two mean opposite things to the driver: this one
    /// keeps the producer running, a <see cref="Goto"/> ends the subscription and cancels it. They
    /// were the same case once, which left the driver inferring the difference from whether the
    /// target happened to equal the call activity's own name — true of a resume, and true again of
    /// an author transitioning to that activity on purpose.
    /// </summary>
    public sealed record Resume(
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
