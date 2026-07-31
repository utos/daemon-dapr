using Common.SeedWork;
using Observability.Application.Ports;

namespace Observability.Infrastructure.Store;

/// <summary>
/// Existence is decided by whether the execution's event stream exists. Stream naming stays here,
/// in infrastructure — the application layer only asks the question.
/// </summary>
public class EventStoreExecutionProbe(IEventStoreRepository eventStore) : IExecutionProbe
{
    public Task<bool> ExistsAsync(ExecutionId executionId, CancellationToken ct) =>
        eventStore.ExistsAsync(StreamNames.Execution(executionId), ct);
}
