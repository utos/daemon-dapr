namespace Common.SeedWork;

public interface IEventStoreRepository
{
    Task<T> LoadAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot<TId>, new() where TId : notnull;

    Task SaveAsync<T, TId>(T aggregate) where T : AggregateRoot<TId>, new() where TId : notnull;

    Task AppendEventAsync<T, TId>(TId aggregateId, IDomainEvent @event)
        where T : AggregateRoot<TId>, new() where TId : notnull;

    Task<bool> ExistsAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot<TId>, new() where TId : notnull;

    Task AppendToStreamAsync(string streamName, IDomainEvent @event);

    Task<bool> ExistsAsync(string streamName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a stream. Idempotent: deleting a stream that does not exist is a no-op.
    /// </summary>
    Task DeleteStreamAsync(string streamName, CancellationToken cancellationToken = default);

    IAsyncEnumerable<EventWithNumber> SubscribeToStream<T, TId>(TId aggregateId, long? afterPosition,
        CancellationToken cancellationToken = default)
        where T : AggregateRoot<TId>, new() where TId : notnull;
}
