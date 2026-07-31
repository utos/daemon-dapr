namespace Execution.Domain.Interpreter;

/// <param name="Succeeded">False when the branch execution failed.</param>
public sealed record BranchOutcome(bool Succeeded, object? Result);

/// <summary>How far a promise activity has got as its branches settle.</summary>
public abstract record PromiseProgress
{
    private PromiseProgress()
    {
    }

    /// <summary>Not decided yet — keep feeding branches in.</summary>
    public sealed record Pending : PromiseProgress;

    /// <summary>The mode is satisfied. These are the results to transition with.</summary>
    public sealed record Resolved(Dictionary<string, object?> Results) : PromiseProgress;

    /// <summary>
    /// A branch's own failure decides the outcome, so the driver should surface that
    /// branch's error rather than a message invented here.
    /// </summary>
    public sealed record BranchFailed(string BranchName) : PromiseProgress;

    /// <summary>The mode itself cannot be satisfied, for a reason only the policy knows.</summary>
    public sealed record Failed(string Reason) : PromiseProgress;
}

/// <summary>
/// The <c>all</c>/<c>any</c>/<c>race</c>/<c>count</c> arbitration, as a state machine fed one
/// settled branch at a time. Awaiting branches is the driver's job — which of them decide the
/// outcome is this type's, and keeping the two apart is what makes the modes testable without
/// scheduling anything.
/// </summary>
public sealed class PromiseResolver
{
    private readonly PromiseMode _mode;
    private readonly int _requiredCount;
    private readonly int _branchCount;
    private readonly Dictionary<string, object?> _succeeded = new(StringComparer.Ordinal);

    private int _observed;

    public PromiseResolver(PromiseMode mode, int requiredCount, int branchCount)
    {
        _mode = mode;
        _requiredCount = requiredCount;
        _branchCount = branchCount;
    }

    /// <summary>
    /// The outcome before any branch settles, which decides degenerate cases — a promise
    /// with no branches, or a count that is already met — without awaiting anything.
    /// </summary>
    public PromiseProgress Start()
    {
        return _mode switch
        {
            PromiseMode.All when _branchCount == 0 => Resolved(),
            PromiseMode.Count when _requiredCount <= 0 => Resolved(),
            PromiseMode.Any when _branchCount == 0 => AllBranchesFailed(),
            PromiseMode.Race when _branchCount == 0 => new PromiseProgress.Failed(
                "Promise has no branches to settle (mode: race)"),
            _ => new PromiseProgress.Pending()
        };
    }

    /// <summary>Records a settled branch and reports whether that decides the promise.</summary>
    public PromiseProgress Observe(string branchName, BranchOutcome outcome)
    {
        _observed++;

        if (outcome.Succeeded)
            _succeeded[branchName] = outcome.Result;

        var remaining = _branchCount - _observed;

        switch (_mode)
        {
            // Every branch must succeed, so the first failure decides it.
            case PromiseMode.All:
                if (!outcome.Succeeded)
                    return new PromiseProgress.BranchFailed(branchName);

                return remaining == 0 ? Resolved() : new PromiseProgress.Pending();

            // The first success wins; failures are tolerated until nothing is left.
            case PromiseMode.Any:
                if (outcome.Succeeded)
                    return Resolved(branchName);

                return remaining == 0 ? AllBranchesFailed() : new PromiseProgress.Pending();

            // Whoever settles first decides, success or failure alike.
            case PromiseMode.Race:
                return outcome.Succeeded
                    ? Resolved(branchName)
                    : new PromiseProgress.BranchFailed(branchName);

            case PromiseMode.Count:
                if (_succeeded.Count >= _requiredCount)
                    return Resolved();

                // Once the branches still outstanding cannot make up the shortfall, waiting
                // for them only delays the same failure.
                if (_succeeded.Count + remaining < _requiredCount)
                    return CannotReachCount();

                return new PromiseProgress.Pending();

            default:
                return new PromiseProgress.Failed($"Unsupported promise mode: {_mode}");
        }
    }

    private PromiseProgress Resolved()
    {
        return new PromiseProgress.Resolved(new Dictionary<string, object?>(_succeeded, StringComparer.Ordinal));
    }

    private PromiseProgress Resolved(string branchName)
    {
        return new PromiseProgress.Resolved(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [branchName] = _succeeded[branchName]
        });
    }

    private static PromiseProgress AllBranchesFailed()
    {
        return new PromiseProgress.Failed("All branches failed (mode: any)");
    }

    private PromiseProgress CannotReachCount()
    {
        return new PromiseProgress.Failed($"Cannot reach required count {_requiredCount} (mode: count)");
    }
}
