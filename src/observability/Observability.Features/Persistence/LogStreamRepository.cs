using System.Runtime.CompilerServices;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Extensions;
using Common.SeedWork;
using KurrentDB.Client;
using Microsoft.Extensions.Logging;
using Observability.Domain;

namespace Observability.Features.Persistence;

public class LogStreamRepository(
    KurrentDBClient eventStoreClient,
    IEventSerializer eventSerializer,
    ILogger<LogStreamRepository> logger) : ILogStreamRepository
{
    public async Task AppendAsync(Guid executionId, IDomainEvent logEvent)
    {
        var streamName = StreamNames.ExecutionLog(executionId);
        var serializedEvent = eventSerializer.SerializeEvent(logEvent);

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

            logger.LogDebug("Appended event {EventType} to log stream for execution {ExecutionId}",
                logEvent.GetType().Name, executionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append event {EventType} to log stream for execution {ExecutionId}",
                logEvent.GetType().Name, executionId);
            throw;
        }
    }

    public async IAsyncEnumerable<EventWithNumber> SubscribeAsync(
        Guid executionId, long? afterPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var streamName = StreamNames.ExecutionLog(executionId);

        var fromStream = afterPosition.HasValue
            ? FromStream.After(StreamPosition.FromInt64(afterPosition.Value))
            : FromStream.Start;

        await using var subscription = eventStoreClient.SubscribeToStream(
            streamName,
            fromStream,
            cancellationToken: ct
        );

        await foreach (var message in subscription.Messages.WithCancellation(ct))
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
}
