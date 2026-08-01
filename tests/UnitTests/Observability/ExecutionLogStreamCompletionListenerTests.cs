using Common.SeedWork;
using Observability.Application.Listeners;
using Observability.Domain;

namespace UnitTests.Observability;

/// <summary>
/// What a watching client sees when a run ends. The terminal frame is the only thing that closes
/// the watch stream, so a listener that forwards the wrong outcome — or drops the reason — leaves
/// the client reading a stream that says nothing about how the run finished.
/// </summary>
public class ExecutionLogStreamCompletionListenerTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTime At = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_completed_run_closes_the_stream_with_no_failure_reason()
    {
        var store = new RecordingLogStore();

        await new ExecutionLogStreamCompletionListener(store).OnCompletedAsync(Id, At);

        Assert.Equal([(Id, At, ExecutionOutcome.Completed, null)], store.Completions);
    }

    [Fact]
    public async Task A_failed_run_closes_the_stream_carrying_its_reason()
    {
        var store = new RecordingLogStore();

        await new ExecutionLogStreamCompletionListener(store).OnFailedAsync(Id, At, "HTTP 500");

        Assert.Equal([(Id, At, ExecutionOutcome.Failed, "HTTP 500")], store.Completions);
    }

    private sealed class RecordingLogStore : IExecutionLogStore
    {
        public List<(ExecutionId, DateTime, ExecutionOutcome, string?)> Completions { get; } = [];

        public void CompleteWith(ExecutionId executionId, DateTime timestamp, ExecutionOutcome outcome, string? failureReason)
            => Completions.Add((executionId, timestamp, outcome, failureReason));

        public void AddEntry(ExecutionId executionId, ExecutionLogEntry entry) => throw new NotSupportedException();
        public void Complete(ExecutionId executionId) => throw new NotSupportedException();

        public IAsyncEnumerable<ExecutionStreamItem> SubscribeAsync(
            ExecutionId executionId, int? after, int? tail, CancellationToken ct) => throw new NotSupportedException();
    }
}
