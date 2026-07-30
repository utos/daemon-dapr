using System.Runtime.CompilerServices;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Extensions;
using Common.SeedWork;
using KurrentDB.Client;
using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.Persistence;

public class EventStoreRepository(
    KurrentDBClient eventStoreClient,
    IEventSerializer eventSerializer,
    ILogger<EventStoreRepository> logger) : IEventStoreRepository
{
    public async Task<T> LoadAsync<T>(Guid aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot, new()
    {
        var streamName = GetStreamName<T>(aggregateId);
        var aggregate = new T();

        try
        {
            var events = eventStoreClient.ReadStreamAsync(
                Direction.Forwards,
                streamName,
                StreamPosition.Start,
                cancellationToken: cancellationToken);

            var domainEvents = new List<IDomainEvent>();
            await foreach (var resolvedEvent in events.WithCancellation(cancellationToken))
            {
                var domainEvent = eventSerializer.DeserializeEvent(
                    resolvedEvent.Event.EventType,
                    resolvedEvent.Event.Data.ToArray(),
                    resolvedEvent.Event.Metadata.ToArray());
                if (domainEvent is IDomainEvent de) domainEvents.Add(de);
            }

            aggregate.LoadFromHistory(domainEvents);

            return aggregate;
        }
        catch (StreamNotFoundException)
        {
            logger.LogWarning("Stream {StreamName} not found for aggregate {AggregateId}",
                streamName, aggregateId);
            throw new AggregateNotFoundException($"Aggregate {aggregateId} not found");
        }
    }

    public async Task SaveAsync<T>(T aggregate) where T : AggregateRoot, new()
    {
        var streamName = GetStreamName<T>(aggregate.Id);

        var uncommittedEvents = aggregate.GetDomainEvents();

        if (uncommittedEvents.Count == 0)
        {
            logger.LogDebug("No uncommitted changes for aggregate {AggregateId}", aggregate.Id);
            return;
        }

        var eventData = uncommittedEvents
            .Select(domainEvent =>
            {
                var serializedEvent = eventSerializer.SerializeEvent(domainEvent);
                return new EventData(
                    Uuid.NewUuid(),
                    serializedEvent.EventType,
                    serializedEvent.Data,
                    serializedEvent.Metadata);
            })
            .ToArray();

        try
        {
            await eventStoreClient.AppendToStreamAsync(
                streamName,
                StreamState.Any,
                eventData);

            aggregate.ClearDomainEvents();

            logger.LogDebug("Saved {EventCount} events for aggregate {AggregateId}",
                eventData.Length, aggregate.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save events for aggregate {AggregateId}", aggregate.Id);
            throw;
        }
    }

    public async Task AppendEventAsync<T>(Guid aggregateId, IDomainEvent @event) where T : AggregateRoot, new()
    {
        var streamName = GetStreamName<T>(aggregateId);
        var serializedEvent = eventSerializer.SerializeEvent(@event);

        var eventData = new EventData(
            Uuid.NewUuid(),
            serializedEvent.EventType,
            serializedEvent.Data,
            serializedEvent.Metadata);

        try
        {
            await eventStoreClient.AppendToStreamAsync(
                streamName,
                StreamState.Any,
                new[] { eventData });

            logger.LogDebug("Appended event {EventType} to aggregate {AggregateId}",
                @event.GetType().Name, aggregateId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append event {EventType} to aggregate {AggregateId}",
                @event.GetType().Name, aggregateId);
            throw;
        }
    }

    public async Task AppendToStreamAsync(string streamName, IDomainEvent @event)
    {
        var serializedEvent = eventSerializer.SerializeEvent(@event);

        var eventData = new EventData(
            Uuid.NewUuid(),
            serializedEvent.EventType,
            serializedEvent.Data,
            serializedEvent.Metadata);

        try
        {
            await eventStoreClient.AppendToStreamAsync(
                streamName,
                StreamState.Any,
                new[] { eventData });

            logger.LogDebug("Appended event {EventType} to stream {StreamName}",
                @event.GetType().Name, streamName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append event {EventType} to stream {StreamName}",
                @event.GetType().Name, streamName);
            throw;
        }
    }

    public async Task DeleteStreamAsync(string streamName, CancellationToken cancellationToken = default)
    {
        try
        {
            await eventStoreClient.DeleteAsync(
                streamName,
                StreamState.Any,
                cancellationToken: cancellationToken);

            logger.LogDebug("Deleted stream {StreamName}", streamName);
        }
        // Deleting an absent stream with StreamState.Any surfaces as WrongExpectedVersion
        // (expected Any, actual NoStream), not StreamNotFound. Since Any is our only version
        // constraint, this can only mean the stream does not exist — an idempotent no-op
        // (e.g. a run with no sub-workflow calls has no subworkflowcalls-{id} stream).
        catch (WrongExpectedVersionException)
        {
            logger.LogDebug("Stream {StreamName} does not exist; nothing to delete", streamName);
        }
        catch (StreamNotFoundException)
        {
            logger.LogDebug("Stream {StreamName} not found; nothing to delete", streamName);
        }
    }

    public async Task<bool> ExistsAsync<T>(Guid aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot, new()
    {
        var streamName = GetStreamName<T>(aggregateId);

        try
        {
            // Try to read from the stream - recommended pattern from KurrentDB docs
            var result = eventStoreClient.ReadStreamAsync(
                Direction.Forwards,
                streamName,
                StreamPosition.Start,
                1,
                cancellationToken: cancellationToken);

            // Check if stream exists by examining ReadState
            if (await result.ReadState != ReadState.StreamNotFound) return true;

            logger.LogDebug("Stream {StreamName} not found for aggregate {AggregateId}",
                streamName, aggregateId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking existence of stream {StreamName} for aggregate {AggregateId}",
                streamName, aggregateId);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string streamName, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = eventStoreClient.ReadStreamAsync(
                Direction.Forwards,
                streamName,
                StreamPosition.Start,
                1,
                cancellationToken: cancellationToken);

            if (await result.ReadState != ReadState.StreamNotFound) return true;

            logger.LogDebug("Stream {StreamName} not found", streamName);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking existence of stream {StreamName}", streamName);
            throw;
        }
    }

    public async IAsyncEnumerable<EventWithNumber> SubscribeToStream<T>(
        Guid aggregateId,
        long? afterPosition,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : AggregateRoot, new()
    {
        var streamName = GetStreamName<T>(aggregateId);

        var fromStream = afterPosition.HasValue
            ? FromStream.After(StreamPosition.FromInt64(afterPosition.Value))
            : FromStream.Start;

        await using var subscription = eventStoreClient.SubscribeToStream(
            streamName,
            fromStream,
            cancellationToken: cancellationToken
        );

        await foreach (var message in subscription.Messages.WithCancellation(cancellationToken))
        {
            if (message is not StreamMessage.Event @event)
                continue;

            var domainEvent = eventSerializer.DeserializeEvent(
                @event.GetEventType(),
                @event.GetEventData(),
                @event.GetMetadata()
            );

            if (domainEvent != null) yield return new EventWithNumber(domainEvent, @event.GetEventNumber());
        }
    }

    private static string GetStreamName<T>(Guid aggregateId) where T : AggregateRoot
    {
        var aggregateType = typeof(T).Name.ToLowerInvariant();
        return $"{aggregateType}-{aggregateId}";
    }
}
