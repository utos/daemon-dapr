namespace Execution.Domain.Interpreter;

/// <summary>Where an activity's outcome sends the execution next.</summary>
public abstract record TransitionDecision
{
    private TransitionDecision()
    {
    }

    /// <summary>
    /// The rule that produced this decision. Null when no rule matched — an exhausted list's
    /// <see cref="End"/> or <see cref="Propagate"/>, or the <see cref="Resume"/> an unmatched
    /// emission falls back to — because then no rule decided anything.
    /// </summary>
    public RuleMatch? Match { get; init; }

    /// <summary>Continue at <paramref name="ActivityName"/> with <paramref name="Input"/>.</summary>
    public sealed record Goto(string ActivityName, Dictionary<string, object>? Input) : TransitionDecision;

    /// <summary>
    /// End this path, producing <paramref name="Value"/> as its result — or, when null, no result
    /// at all: an empty <c>result</c> is a bare <c>return</c>, and the run ends exactly as it does
    /// when its rules run out. The two are one case because on the emitted path they mean the same
    /// thing to the driver — the consumer is done consuming — where <see cref="End"/> never occurs.
    /// </summary>
    public sealed record Return(object? Value) : TransitionDecision;

    /// <summary>
    /// End this path as a failure, with the reason the rule's <c>error</c> action gave it —
    /// <paramref name="Message"/> and <paramref name="Details"/> already rendered. The code is the
    /// author's literal; it is what the run reports and what a caller's <c>on_failure</c> matches.
    /// </summary>
    public sealed record Fail(
        string Code,
        string Message,
        Dictionary<string, object>? Details) : TransitionDecision;

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
