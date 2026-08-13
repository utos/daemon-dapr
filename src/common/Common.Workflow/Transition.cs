namespace Common.Workflow;

/// <summary>
/// Where control goes after an activity. Rules are evaluated in order; the first whose
/// <see cref="Condition"/> holds wins, and a rule with no condition always matches.
///
/// Exactly one of <see cref="Transition"/>, <see cref="Result"/> and <see cref="Emit"/> is set —
/// the run moves to another activity, it returns a value and ends, or it appends a value to its
/// own output stream and carries on.
/// </summary>
public sealed record TransitionRule(
    TransitionTarget? Transition = null,
    ValueMap? Result = null,
    EmitAction? Emit = null,
    string? Condition = null)
{
    /// <summary>False when a rule was authored with no action — rejected by the interpreter.</summary>
    public bool HasAction => Transition != null || Result != null || Emit != null;

    public static TransitionRule Goto(string name, ValueMap? input = null, string? condition = null) =>
        new(new TransitionTarget(name, input), Condition: condition);

    public static TransitionRule Returns(ValueMap result, string? condition = null) =>
        new(Result: result, Condition: condition);

    public static TransitionRule Emits(
        ValueMap value, string target, ValueMap? input = null, string? condition = null) =>
        new(Emit: new EmitAction(value, new TransitionTarget(target, input)), Condition: condition);
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
/// </summary>
public sealed record TransitionTarget(string Name, ValueMap? Input = null);
