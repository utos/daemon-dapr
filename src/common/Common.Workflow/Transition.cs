namespace Common.Workflow;

/// <summary>
/// Where control goes after an activity. Rules are evaluated in order; the first whose
/// <see cref="Condition"/> holds wins, and a rule with no condition always matches.
///
/// Exactly one of <see cref="Transition"/>, <see cref="Result"/>, <see cref="Emit"/> and
/// <see cref="Error"/> is set — the run moves to another activity, it returns a value and ends,
/// it appends a value to its own output stream and carries on, or it fails with a reason.
/// </summary>
/// <param name="Result">
/// An empty map is a complete action: it ends the path with no value. The source format spells
/// both as <c>return</c>.
/// </param>
/// <param name="Error">
/// Declared last so the positional callers written before it exist unchanged, and so that the
/// persisted JSON — where a null it appends after <c>Condition</c> is the whole difference — reads
/// back on every rule written before it.
/// </param>
public sealed record TransitionRule(
    TransitionTarget? Transition = null,
    ValueMap? Result = null,
    EmitAction? Emit = null,
    string? Condition = null,
    ErrorAction? Error = null)
{
    // `HasAction` used to live here and was never read. See the note on EmissionRule for why a
    // computed property on one of these is a persistence decision rather than a convenience:
    // UTOS-T001 is the rule that rejects an actionless transition rule, and the interpreter's
    // `_ =>` arm is the backstop for a bundle that reached it anyway.

    public static TransitionRule Goto(string name, ValueMap? input = null, string? condition = null) =>
        new(new TransitionTarget(name, input), Condition: condition);

    public static TransitionRule Returns(ValueMap result, string? condition = null) =>
        new(Result: result, Condition: condition);

    /// <summary>Ends the path with no value — a bare <c>return</c> in the source format.</summary>
    public static TransitionRule Ends(string? condition = null) =>
        new(Result: ValueMap.Empty, Condition: condition);

    public static TransitionRule Emits(
        ValueMap value, string target, ValueMap? input = null, string? condition = null) =>
        new(Emit: new EmitAction(value, new TransitionTarget(target, input)), Condition: condition);

    public static TransitionRule Fails(
        string code, string? message = null, ValueMap? details = null, string? condition = null) =>
        new(Error: new ErrorAction(code, message, details), Condition: condition);
}

/// <summary>
/// Appends <see cref="Value"/> to this execution's output stream and then continues at
/// <see cref="Transition"/>. <c>result</c> is emit-and-terminate; this is emit-and-continue, so a
/// workflow that emits N times and then returns produces N value entries followed by one terminal
/// entry in the same ordered stream.
/// </summary>
/// <param name="Transition">
/// Nullable for the same reason a timer's duration is: the wire format permits a rule authored
/// with no target, and that has to survive mapping long enough for <c>UTOS-T004</c> to reject it
/// by name. Non-null on anything that passed load-time validation.
/// </param>
public sealed record EmitAction(ValueMap? Value, TransitionTarget? Transition);

/// <summary>
/// The activity to move to. <see cref="Input"/>, when set, is the transform that builds that
/// activity's input; without it the previous activity's output carries over unchanged.
/// <para>
/// Always an activity. Until spec 0.0.15 the name could also be the keyword <c>end</c> or
/// <c>error</c>; those are the <c>result</c> and <c>error</c> actions now, and the shared validator
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
