namespace Execution.Domain.Aggregates;

/// <summary>
/// A lifecycle transition the orchestration asks for. Named rather than stringly-typed so the
/// set is closed and the compiler catches a caller the daemon cannot serve.
/// </summary>
public enum ExecutionLifecycleTransition
{
    Active,
    Completed,
    Failed,
    Cancelled
}
