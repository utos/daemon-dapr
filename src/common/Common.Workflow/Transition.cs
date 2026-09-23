using System.Text.Json.Serialization;

namespace Common.Workflow;

/// <summary>
/// One rule, in any list: a condition, at most one <see cref="Effect"/> — what happens — and at
/// most one <see cref="Exit"/> — where the run goes next. Rules are evaluated in order; the first
/// whose <see cref="Condition"/> holds wins, and a rule with no condition always matches.
/// <para>
/// One type serves every list, <c>onEmitted</c> included, where there used to be a second
/// (<c>EmissionRule</c>) that differed only in which actions it allowed. The lists differ in one
/// thing and the spec now says it in one place: <c>onSuccess</c> and <c>onFailure</c> end the
/// activity, so a rule there must have an exit, while an <c>onEmitted</c> rule runs once per value
/// and its exit is what *stops* consuming. A rule with neither an effect nor an exit is rejected
/// at load (<c>UTOS-T001</c>).
/// </para>
/// <para>
/// Effect and exit are separate fields rather than one action because they are independent
/// questions: <c>emit</c> then <c>return</c>, or <c>workflow.call</c> then <c>transition</c>, are
/// rules an author can now write and neither was expressible while a rule carried exactly one
/// action. See <c>api/docs/workflow-source-format.md</c> § Transitions.
/// </para>
/// </summary>
public sealed record TransitionRule(
    string? Condition = null,
    RuleEffect? Effect = null,
    RuleExit? Exit = null)
{
    // No `HasAction` here, deliberately: a computed property on a record that reaches the event
    // stream is *persisted*, so it pins wider text in ActivityConfigJsonShapeTests and would
    // become a stale claim on already-written events if the derivation ever changed. UTOS-T001 is
    // the rule that rejects a rule with neither half, and the interpreter's backstop is what
    // catches a bundle that reached it anyway.

    public static TransitionRule Goto(string name, ValueMap? input = null, string? condition = null) =>
        new(condition, Exit: new RuleExit.Goto(new TransitionTarget(name, input)));

    public static TransitionRule Returns(ValueMap result, string? condition = null) =>
        new(condition, Exit: new RuleExit.Return(result));

    /// <summary>Ends the path with no value — a bare <c>return</c> in the source format.</summary>
    public static TransitionRule Ends(string? condition = null) =>
        new(condition, Exit: new RuleExit.Return(ValueMap.Empty));

    /// <summary>Emit-and-continue: append <paramref name="value"/>, then go to <paramref name="target"/>.</summary>
    public static TransitionRule Emits(
        ValueMap value, string target, ValueMap? input = null, string? condition = null) =>
        new(condition,
            new RuleEffect.Emit(value),
            new RuleExit.Goto(new TransitionTarget(target, input)));

    /// <summary>Emit and stop: the value is appended, then the path ends with no result.</summary>
    public static TransitionRule EmitsAndEnds(ValueMap value, string? condition = null) =>
        new(condition, new RuleEffect.Emit(value), new RuleExit.Return(ValueMap.Empty));

    /// <summary>
    /// Run a document and, with no exit, come back for the next value. The body of a consuming
    /// loop — what <c>EmissionRule.Handles</c> was before one rule type served every list.
    /// </summary>
    public static TransitionRule Calls(
        string workflow, string startActivity, ValueMap? input = null, string? condition = null) =>
        new(condition, new RuleEffect.Call(new HandlerDispatch(workflow, startActivity, input)));

    public static TransitionRule Fails(
        string code, string? message = null, ValueMap? details = null, string? condition = null) =>
        new(condition, Exit: new RuleExit.Fail(new ErrorAction(code, message, details)));
}

/// <summary>
/// What a rule <em>does</em> when it matches, as against where it sends the run. At most one, and
/// a closed hierarchy rather than a set of nullable fields so that "two effects" is not a state
/// the domain can hold — two would need an order the document cannot express.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "effect")]
[JsonDerivedType(typeof(RuleEffect.Emit), "emit")]
[JsonDerivedType(typeof(RuleEffect.Call), "workflow.call")]
public abstract record RuleEffect
{
    private RuleEffect()
    {
    }

    /// <summary>
    /// Append <see cref="Value"/> to this execution's output stream and carry on. Emit-and-continue,
    /// as against a <see cref="RuleExit.Return"/>'s emit-and-terminate — and now composable with
    /// one, which is how a run says "here is the last item, and here is the summary".
    /// </summary>
    /// <param name="Value">
    /// Nullable because the wire permits an <c>emit</c> authored with no fields, which renders to
    /// the empty map. Distinct from a <see cref="RuleExit.Return"/>'s empty map, which means "no
    /// value at all": an emitted entry always exists, whatever it holds.
    /// </param>
    public sealed record Emit(ValueMap? Value) : RuleEffect;

    /// <summary>
    /// Run a document and wait for it to finish, then take this rule's exit if it has one.
    /// <para>
    /// In an <c>onEmitted</c> rule with no exit this is the body of the consuming loop: the call
    /// finishes one iteration and the next value is taken. A dispatched document cannot wander
    /// into this workflow's own flow — that is what keeps "ran out of transitions" meaning one
    /// thing inside a handler and another outside it — so where the consumer goes afterwards is
    /// decided here, by the consumer's own exit, and never by the handler.
    /// </para>
    /// </summary>
    public sealed record Call(HandlerDispatch Dispatch) : RuleEffect;
}

/// <summary>
/// Where the run goes when a rule matches. At most one; absent means the rule does not end the
/// activity, which is legal only in an <c>onEmitted</c> list and there means "take the next value".
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "exit")]
[JsonDerivedType(typeof(RuleExit.Goto), "transition")]
[JsonDerivedType(typeof(RuleExit.Return), "result")]
[JsonDerivedType(typeof(RuleExit.Fail), "error")]
public abstract record RuleExit
{
    private RuleExit()
    {
    }

    /// <summary>Continue at another activity in this workflow.</summary>
    public sealed record Goto(TransitionTarget Target) : RuleExit;

    /// <summary>
    /// End this execution path, returning <see cref="Value"/>. An empty map is a complete exit: it
    /// ends the path with no value. The source format spells both as <c>return</c>.
    /// </summary>
    public sealed record Return(ValueMap Value) : RuleExit;

    /// <summary>End this execution path as a failure, with the reason the author gave it.</summary>
    public sealed record Fail(ErrorAction Error) : RuleExit;
}

/// <summary>
/// The activity to move to. <see cref="Input"/>, when set, is the transform that builds that
/// activity's input; without it the previous activity's output carries over unchanged.
/// <para>
/// Always an activity. Until spec 0.0.15 the name could also be the keyword <c>end</c> or
/// <c>error</c>; those are the <c>result</c> and <c>error</c> exits now, and the shared validator
/// refuses a document that still uses them (<c>UTOS-T003</c>).
/// </para>
/// </summary>
public sealed record TransitionTarget(string Name, ValueMap? Input = null);

/// <summary>
/// Ends the path as a failure, with the reason the author gave it. The same shape the run
/// reports (<c>utos.workflow.v1.WorkflowError</c>): <see cref="Code"/> is a literal identifier, the
/// thing a consumer or an <c>on_failure</c> rule matches on; <see cref="Message"/> is a text
/// template and <see cref="Details"/> a struct template, both rendered in the rule's scope.
/// </summary>
/// <param name="Code">
/// Required by <c>UTOS-T005</c>. Non-empty on anything that passed load-time validation; the
/// interpreter substitutes a generic code for a bundle that reached it without.
/// </param>
public sealed record ErrorAction(string Code, string? Message = null, ValueMap? Details = null);

/// <summary>
/// A dispatch: run a document, starting from one of its activities.
/// <para>
/// The same three fields a <see cref="PromiseBranch"/> carries, minus the fan-out parts. Kept as
/// its own type rather than folded into <see cref="TransitionTarget"/> because the two answer
/// different questions — a transition names an activity in <em>this</em> workflow and stays
/// inside it, a dispatch names a document and leaves.
/// </para>
/// </summary>
public sealed record HandlerDispatch(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null);
