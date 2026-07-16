namespace Observability.Domain;

public interface IExecutionLogStore
{
    void AddEntry(Guid executionId, ExecutionLogEntry entry);
    void Complete(Guid executionId);
    IAsyncEnumerable<ExecutionLogEntry> SubscribeAsync(Guid executionId, int? after, int? tail, CancellationToken ct);
}
