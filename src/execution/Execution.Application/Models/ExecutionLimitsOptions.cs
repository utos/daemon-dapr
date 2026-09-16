namespace Execution.Application.Models;

/// <summary>
/// Daemon-side bounds on how a single execution runs. The spec leaves this to the daemon:
/// "Bounding total spawned work — concurrency, recursion depth, execution budget — is a daemon
/// responsibility, not a constraint of this spec."
///
/// These are captured into the workflow input when an execution is scheduled rather than
/// read inside the orchestrator, so a run keeps the limits it started with. A Dapr
/// orchestrator must replay identically, and reading mutable configuration mid-run would
/// let replay diverge from the original execution.
///
/// There is deliberately no ceiling on how much work an execution may do. `MaxLoopedTransitions`
/// used to fail a run past 10,000 looped activity executions, and existed only because the daemon
/// had no way to stop a runaway loop; `CancelExecution` is that way, so the ceiling is gone rather
/// than raised. What is bounded is the orchestration runtime's own history, below.
/// </summary>
public class ExecutionLimitsOptions
{
    public const string SectionName = "Execution";

    /// <summary>
    /// Activity transitions performed within one Dapr workflow instance before it continues
    /// as new, which resets the instance history that otherwise grows on every activity call.
    /// Only applied once a loop has been detected: continuing as new discards history, which
    /// is what a poller wants but is destructive for a long non-looping run.
    /// </summary>
    public int ContinueAsNewAfterTransitions { get; set; } = 100;
}
