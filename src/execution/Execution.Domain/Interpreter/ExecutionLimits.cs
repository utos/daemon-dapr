namespace Execution.Domain.Interpreter;

/// <summary>
/// The bounds a single execution runs under. Passed in rather than read from configuration
/// so a run keeps the limits it started with: a durable orchestration must replay
/// identically, and reading mutable configuration mid-run would let replay diverge.
/// </summary>
/// <param name="MaxLoopedTransitions">
/// Activity executions allowed after a loop has been entered, before the execution fails. Zero
/// or less is no ceiling, which is the default an execution is scheduled with — an endless loop
/// is legal, and cancellation is what stops one.
/// </param>
/// <param name="ContinueAsNewAfterTransitions">
/// Activity transitions performed within one orchestration instance before it continues as
/// new, which resets the instance history that otherwise grows on every activity call.
/// </param>
public sealed record ExecutionLimits(int MaxLoopedTransitions, int ContinueAsNewAfterTransitions);
