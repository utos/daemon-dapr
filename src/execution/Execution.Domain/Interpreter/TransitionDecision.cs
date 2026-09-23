namespace Execution.Domain.Interpreter;

/// <summary>
/// What a matched rule does, before it says where the run goes. Null on a decision means the rule
/// had no effect — or that no rule matched at all.
/// <para>
/// Separate from the decision itself because the two are independent in the document: since spec
/// 0.20 a rule carries at most one effect <em>and</em> at most one exit, so "emit this and then
/// return" and "call that and then transition" are ordinary rules. While an effect was one of the
/// actions, each of those needed two rules and a dummy activity between them.
/// </para>
/// </summary>
public abstract record DecisionEffect
{
    private DecisionEffect()
    {
    }

    /// <summary>
    /// Append <paramref name="Value"/> to this execution's output stream. Durable before the run
    /// moves on, so a consumer reading the stream in order can never see the effect of a value it
    /// has not been given yet.
    /// </summary>
    public sealed record Emit(Dictionary<string, object> Value) : DecisionEffect;

    /// <summary>
    /// Run <paramref name="Workflow"/> from <paramref name="StartActivity"/> as a child execution
    /// and wait for it to terminate.
    ///
    /// Distinct from a <see cref="TransitionDecision.Goto"/> because it leaves this workflow:
    /// nothing the dispatched document does can land in this execution's flow, which is what stops
    /// "ran out of transitions" meaning one thing inside a handler and another outside it. Where
    /// this execution goes once the child ends is the rule's own exit, never the handler's.
    /// </summary>
    public sealed record Dispatch(
        string Workflow,
        string StartActivity,
        Dictionary<string, object>? Input) : DecisionEffect;
}

/// <summary>Where an activity's outcome sends the execution next, and what happens on the way.</summary>
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

    /// <summary>
    /// What the matched rule did before going anywhere: appended a value, or ran a document. Null
    /// where the rule had no effect, which is every rule that only says where to go.
    /// </summary>
    public DecisionEffect? Effect { get; init; }

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
    /// End this path as a failure, with the reason the rule's <c>error</c> exit gave it —
    /// <paramref name="Message"/> and <paramref name="Details"/> already rendered. The code is the
    /// author's literal; it is what the run reports and what a caller's <c>on_failure</c> matches.
    /// </summary>
    public sealed record Fail(
        string Code,
        string Message,
        Dictionary<string, object>? Details) : TransitionDecision;

    /// <summary>
    /// Back to <paramref name="ActivityName"/> for the next emitted value, on the same
    /// subscription. What an <c>onEmitted</c> rule with no exit means, and what an unmatched list
    /// falls back to.
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
