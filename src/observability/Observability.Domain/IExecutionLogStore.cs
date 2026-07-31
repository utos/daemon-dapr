namespace Observability.Domain;

public interface IExecutionLogStore
{
    void AddEntry(Guid executionId, ExecutionLogEntry entry);
    void Complete(Guid executionId);

    /// <summary>
    /// Emits a terminal frame carrying the execution outcome (and failure reason, if any)
    /// and then completes the stream. The frame is delivered to subscribers before the
    /// stream closes and is replayed to late subscribers from history.
    /// </summary>
    void CompleteWith(Guid executionId, DateTime timestamp, ExecutionOutcome outcome, string? failureReason);

    IAsyncEnumerable<ExecutionStreamItem> SubscribeAsync(Guid executionId, int? after, int? tail, CancellationToken ct);
}
