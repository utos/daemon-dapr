namespace Execution.Application.Models;

/// <summary>
/// Daemon-side bounds on how much work a single execution may do. The spec leaves this to
/// the daemon: "Bounding total spawned work — concurrency, recursion depth, execution
/// budget — is a daemon responsibility, not a constraint of this spec."
///
/// These are captured into the workflow input when an execution is scheduled rather than
/// read inside the orchestrator, so a run keeps the limits it started with. A Dapr
/// orchestrator must replay identically, and reading mutable configuration mid-run would
/// let replay diverge from the original execution.
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

    /// <summary>
    /// Activity executions allowed *after* a loop has been entered, before the execution
    /// fails. Counted per re-executed activity rather than per lap, so the budget means the
    /// same amount of work whether a loop body has one activity or fifty.
    ///
    /// Only looped work is counted: linear path length is already bounded by the bundle —
    /// you can only walk as many activities as are defined — so a long non-looping workflow
    /// is never failed for its length.
    ///
    /// **Zero — the default — is no ceiling.** The spec permits an endless loop ("runs until the
    /// execution is cancelled"), and since `CancelExecution` there is a way to stop one, which is
    /// what a ceiling stood in for. A workflow consuming a sub-workflow's output stream re-enters
    /// its call activity once per emitted value, so any finite ceiling is a countdown to killing
    /// a long-lived watcher for doing exactly its job. The knob stays so an operator can re-impose
    /// one without a code change; cancellation, not a counter, is what stops a runaway.
    /// </summary>
    public int MaxLoopedTransitions { get; set; }
}
