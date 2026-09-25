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

    /// <summary>
    /// The run was stopped on purpose. A third ending rather than a failure with a reason, because
    /// the two are not the same fact and a listener that treats them alike should say so by writing
    /// one line.
    /// </summary>
    /// <remarks>
    /// The one method here with a default body, and deliberately: what a <em>cancelled</em> run's
    /// terminal watch frame should say is an open question with a wire shape attached
    /// (<c>ExecutionOutcome</c> has only <c>Completed</c> and <c>Failed</c>), and answering it is not
    /// retention's business. So the observability listener keeps the behaviour it had, which is to
    /// leave the log stream open on a cancel — a gap that predates this method and is not made worse
    /// by it. Retention cannot afford the same silence: a cancelled tree whose ending nothing records
    /// keeps its intermediate blobs until the sweep's age backstop finds them weeks later.
    /// </remarks>
    /// <param name="reason">Why, when anyone said. Null is an ordinary thing for a caller to mean.</param>
    Task OnCancelledAsync(
        ExecutionId executionId, DateTime occurredAt, string? reason, CancellationToken ct = default) =>
        Task.CompletedTask;
}
