namespace Common.Workflow;

/// <summary>
/// Where control goes after an activity. Rules are evaluated in order; the first whose
/// <see cref="Condition"/> holds wins, and a rule with no condition always matches.
///
/// Exactly one of <see cref="Transition"/> and <see cref="Result"/> is set — either the run
/// moves to another activity, or it returns a value and ends.
/// </summary>
public sealed record TransitionRule(
    TransitionTarget? Transition = null,
    ValueMap? Result = null,
    string? Condition = null)
{
    /// <summary>False when a rule was authored with neither action — rejected by the interpreter.</summary>
    public bool HasAction => Transition != null || Result != null;

    public static TransitionRule Goto(string name, ValueMap? input = null, string? condition = null) =>
        new(new TransitionTarget(name, input), Condition: condition);

    public static TransitionRule Returns(ValueMap result, string? condition = null) =>
        new(Result: result, Condition: condition);
}

/// <summary>
/// The activity to move to. <see cref="Input"/>, when set, is the transform that builds that
/// activity's input; without it the previous activity's output carries over unchanged.
/// </summary>
public sealed record TransitionTarget(string Name, ValueMap? Input = null);
