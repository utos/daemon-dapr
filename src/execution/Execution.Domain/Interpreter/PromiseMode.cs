namespace Execution.Domain.Interpreter;

/// <summary>How a promise activity decides it is done.</summary>
public enum PromiseMode
{
    /// <summary>Every branch must succeed; all results are returned.</summary>
    All,

    /// <summary>The first branch to succeed wins; failures are tolerated until none are left.</summary>
    Any,

    /// <summary>The first branch to settle wins, whether it succeeded or failed.</summary>
    Race,

    /// <summary>The first <c>required_count</c> branches to succeed win.</summary>
    Count
}

public static class PromiseModes
{
    public const string All = "all";
    public const string Any = "any";
    public const string Race = "race";
    public const string Count = "count";

    /// <summary>The mode names accepted in a workflow definition.</summary>
    public static readonly IReadOnlyCollection<string> Names = [All, Any, Race, Count];

    public static bool IsKnown(string? mode)
    {
        return TryParse(mode, out _);
    }

    public static bool TryParse(string? mode, out PromiseMode parsed)
    {
        switch (mode)
        {
            case All:
                parsed = PromiseMode.All;
                return true;
            case Any:
                parsed = PromiseMode.Any;
                return true;
            case Race:
                parsed = PromiseMode.Race;
                return true;
            case Count:
                parsed = PromiseMode.Count;
                return true;
            default:
                parsed = default;
                return false;
        }
    }
}
