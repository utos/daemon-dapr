namespace Execution.Domain.Interpreter;

/// <summary>
/// A promise activity's branches after conditions have been applied and <c>for_each</c> has
/// been expanded.
/// </summary>
public sealed record PromiseExpansion(
    PromiseMode Mode,
    int RequiredCount,
    IReadOnlyList<PromiseBranchPlan> Branches);

/// <summary>
/// One branch to start. Deliberately carries no execution id: minting one is a
/// non-deterministic act that belongs to the durable driver, which has a replay-safe source
/// for it.
/// </summary>
public sealed record PromiseBranchPlan(
    string BranchName,
    string WorkflowKey,
    string StartActivity,
    Dictionary<string, object>? Input);
