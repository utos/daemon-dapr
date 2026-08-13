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
}
