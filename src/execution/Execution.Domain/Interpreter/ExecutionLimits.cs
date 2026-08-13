namespace Execution.Domain.Interpreter;

/// <summary>
/// The bounds a single execution runs under. Passed in rather than read from configuration
/// so a run keeps the limits it started with: a durable orchestration must replay
/// identically, and reading mutable configuration mid-run would let replay diverge.
///
/// There is one bound, and it is not a budget. Nothing caps how much work an execution may do —
/// the spec permits a loop that "runs until the execution is cancelled", and cancellation is what
/// stops one. What must stay bounded is the orchestration runtime's own history, which grows on
/// every activity call, and continuing as new is what resets it.
/// </summary>
/// <param name="ContinueAsNewAfterTransitions">
/// Activity transitions performed within one orchestration instance before it continues as
/// new, which resets the instance history that otherwise grows on every activity call.
/// </param>
public sealed record ExecutionLimits(int ContinueAsNewAfterTransitions);
