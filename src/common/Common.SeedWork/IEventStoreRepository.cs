namespace Common.SeedWork;

public interface IEventStoreRepository
{
    Task<T> LoadAsync<T>(Guid aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot, new();

    Task SaveAsync<T>(T aggregate) where T : AggregateRoot, new();

    Task AppendEventAsync<T>(Guid aggregateId, IDomainEvent @event) where T : AggregateRoot, new();

    Task<bool> ExistsAsync<T>(Guid aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot, new();

    Task AppendToStreamAsync(string streamName, IDomainEvent @event);

    Task<bool> ExistsAsync(string streamName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a stream. Idempotent: deleting a stream that does not exist is a no-op.
    /// </summary>
    Task DeleteStreamAsync(string streamName, CancellationToken cancellationToken = default);

    IAsyncEnumerable<EventWithNumber> SubscribeToStream<T>(Guid aggregateId, long? afterPosition,
        CancellationToken cancellationToken = default) where T : AggregateRoot, new();
}
