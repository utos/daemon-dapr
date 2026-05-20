using Common.SeedWork;

namespace Observability.Domain;

public interface ILogStreamRepository
{
    Task AppendAsync(Guid executionId, IDomainEvent logEvent);

    IAsyncEnumerable<EventWithNumber> SubscribeAsync(
        Guid executionId, long? afterPosition, CancellationToken ct = default);
}
