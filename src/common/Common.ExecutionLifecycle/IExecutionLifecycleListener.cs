using Common.SeedWork;

namespace Common.ExecutionLifecycle;

/// <summary>
/// Notified when a run reaches a terminal state. Execution announces the fact without knowing who
/// wants it, and a listener learns a run ended without knowing what an execution aggregate is or
/// where its events are stored.
///
/// Two methods rather than one outcome-carrying record: the two endings do not carry the same
/// information, and a listener that cares about only one should not have to branch to say so.
/// </summary>
/// <remarks>
/// Called from the execution module's event subscription, inside the scope it opens per event and
/// on the same await chain: an implementation that throws parks the event, and one that blocks
/// stalls the subscription. Do the small thing, or hand off.
/// </remarks>
public interface IExecutionLifecycleListener
{
    Task OnCompletedAsync(ExecutionId executionId, DateTime occurredAt, CancellationToken ct = default);

    /// <param name="code">
    /// The failure's code — an authored <c>error</c> action's, or null when the daemon raised the
    /// failure itself. Structured details are deliberately not carried here: a listener that
    /// needs them reads the execution's output stream, which is the record.
    /// </param>
    Task OnFailedAsync(
        ExecutionId executionId, DateTime occurredAt, string reason, string? code = null,
        CancellationToken ct = default);
}
