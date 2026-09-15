using Common.SeedWork;

namespace Observability.Domain;

public interface IExecutionLogStore
{
    void AddEntry(ExecutionId executionId, ExecutionLogEntry entry);
    void Complete(ExecutionId executionId);

    /// <summary>
    /// Emits a terminal frame carrying the execution outcome (and failure reason and code, if
    /// any) and then completes the stream. The frame is delivered to subscribers before the
    /// stream closes and is replayed to late subscribers from history.
    /// </summary>
    void CompleteWith(
        ExecutionId executionId, DateTime timestamp, ExecutionOutcome outcome, string? failureReason,
        string? failureCode = null);

    IAsyncEnumerable<ExecutionStreamItem> SubscribeAsync(ExecutionId executionId, int? after, int? tail, CancellationToken ct);
}
