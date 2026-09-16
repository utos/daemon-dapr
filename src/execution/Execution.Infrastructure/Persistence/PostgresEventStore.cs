using System.Runtime.CompilerServices;
using System.Text;
using Common.Infrastructure.EventStore;
using Common.SeedWork;
using Execution.Infrastructure.EventProcessing;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Execution.Infrastructure.Persistence;

/// <summary>
/// The event store, on the <c>events</c> table. A stream is the rows sharing a name, ordered by
/// revision; <c>$all</c> is the table ordered by position. The serializer's output is stored as
/// it was produced, so <c>EventJsonShapeTests</c> pins what the rows contain.
/// <para>
/// Every append runs under the global advisory lock (<see cref="EventTables.AppendLock"/>) for
/// the length of its transaction. That serialises appends, which is what makes an expected
/// revision a plain comparison rather than a race, and makes the global position order equal
/// commit order — the property a subscriber tailing by position depends on.
/// </para>
/// </summary>
public sealed class PostgresEventStore(
    NpgsqlDataSource dataSource,
    IEventSerializer eventSerializer,
    EventNotifications notifications,
    ILogger<PostgresEventStore> logger) : IEventStoreRepository
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<T> LoadAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot<TId>, new() where TId : notnull
    {
        var streamName = StreamNames.For<T>(aggregateId);
        var aggregate = new T();

        var domainEvents = new List<IDomainEvent>();
        // Taken from the store rather than counted from the events: the revision is what the
        // next append is checked against, and it is the store's fact, not ours.
        var streamRevision = AggregateRoot<TId>.NoStream;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select revision, event_type, data, metadata from {EventTables.Events} where stream = $1 order by revision",
            connection);
        command.Parameters.AddWithValue(streamName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            streamRevision = reader.GetInt64(0);

            // Deliberately unguarded. An event that cannot be read fails the load, because the
            // alternative — skipping it — rehydrates the aggregate with a hole in its history at
            // the correct revision, and the next append then passes its expected-version check
            // and makes the hole permanent.
            domainEvents.Add(Deserialize(reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        if (domainEvents.Count == 0)
        {
            logger.LogWarning("Stream {StreamName} not found for aggregate {AggregateId}", streamName, aggregateId);
            throw new AggregateNotFoundException($"Aggregate {aggregateId} not found");
        }

        aggregate.LoadFromHistory(domainEvents, streamRevision);
        return aggregate;
    }

    public async Task SaveAsync<T, TId>(T aggregate)
        where T : AggregateRoot<TId>, new() where TId : notnull
    {
        var streamName = StreamNames.For<T>(aggregate.Id);
        var uncommitted = aggregate.GetDomainEvents();

        if (uncommitted.Count == 0)
        {
            logger.LogDebug("No uncommitted changes for aggregate {AggregateId}", aggregate.Id);
            return;
        }

        // Expected revision rather than Any: the aggregate decided what to append by reading the
        // stream, so if the stream has moved since, that decision was made against stale state
        // and must be reconsidered rather than blindly applied.
        try
        {
            await AppendAsync(streamName, uncommitted, expectedRevision: aggregate.Version, CancellationToken.None);
        }
        catch (ConcurrencyException)
        {
            logger.LogDebug(
                "Concurrent write to aggregate {AggregateId}: expected revision {ExpectedRevision}, stream had moved on",
                aggregate.Id, aggregate.Version);
            throw;
        }

        aggregate.MarkCommitted(uncommitted.Count);
        aggregate.ClearDomainEvents();

        logger.LogDebug("Saved {EventCount} events for aggregate {AggregateId}", uncommitted.Count, aggregate.Id);
    }

    public Task AppendEventAsync<T, TId>(TId aggregateId, IDomainEvent @event)
        where T : AggregateRoot<TId>, new() where TId : notnull =>
        AppendToStreamAsync(StreamNames.For<T>(aggregateId), @event);

    public async Task AppendToStreamAsync(string streamName, IDomainEvent @event)
    {
        await AppendAsync(streamName, [@event], expectedRevision: null, CancellationToken.None);
        logger.LogDebug("Appended event {EventType} to stream {StreamName}", @event.GetType().Name, streamName);
    }

    /// <summary>
    /// Physical, unlike KurrentDB's soft delete — which was only ever a way to reclaim space
    /// later, at a scavenge. Idempotent by nature: deleting rows that are not there deletes
    /// nothing, which is what makes a delete retried after a partial failure safe.
    /// </summary>
    public async Task DeleteStreamAsync(string streamName, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"delete from {EventTables.Events} where stream = $1", connection);
        command.Parameters.AddWithValue(streamName);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogDebug("Deleted stream {StreamName} ({EventCount} events)", streamName, deleted);
    }

    public Task<bool> ExistsAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
        where T : AggregateRoot<TId>, new() where TId : notnull =>
        ExistsAsync(StreamNames.For<T>(aggregateId), cancellationToken);

    public async Task<bool> ExistsAsync(string streamName, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select exists(select 1 from {EventTables.Events} where stream = $1)", connection);
        command.Parameters.AddWithValue(streamName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Reads what the stream holds after <paramref name="afterPosition"/>, then waits for more.
    /// Never ends on its own, as a KurrentDB stream subscription does not; the caller's token
    /// is what ends it.
    /// </summary>
    public async IAsyncEnumerable<EventWithNumber> SubscribeToStream<T, TId>(
        TId aggregateId,
        long? afterPosition,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : AggregateRoot<TId>, new() where TId : notnull
    {
        var streamName = StreamNames.For<T>(aggregateId);
        var last = afterPosition ?? -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (var (revision, eventType, data, metadata) in ReadStreamAfterAsync(streamName, last, cancellationToken))
            {
                last = revision;
                yield return new EventWithNumber(Deserialize(eventType, data, metadata), revision);
            }

            await notifications.WaitAsync(streamName, PollInterval, cancellationToken);
        }
    }

    // ---- shared with the output store --------------------------------------------------------

    /// <summary>The rows of one stream after a revision, in order. Empty when the stream does not exist.</summary>
    internal async IAsyncEnumerable<(long Revision, string EventType, string Data, string Metadata)> ReadStreamAfterAsync(
        string streamName, long afterRevision, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select revision, event_type, data, metadata from {EventTables.Events} where stream = $1 and revision > $2 order by revision",
            connection);
        command.Parameters.AddWithValue(streamName);
        command.Parameters.AddWithValue(afterRevision);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    /// <summary>The last row of a stream, or null when the stream does not exist.</summary>
    internal async Task<(long Revision, string EventType, string Data, string Metadata)?> ReadLastAsync(
        string streamName, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select revision, event_type, data, metadata from {EventTables.Events} where stream = $1 order by revision desc limit 1",
            connection);
        command.Parameters.AddWithValue(streamName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    /// <summary>
    /// Appends <paramref name="events"/> to a stream in one transaction, under the global append
    /// lock. With <paramref name="expectedRevision"/> the stream must be exactly there
    /// (<see cref="AggregateRoot{TId}.NoStream"/> for "must not exist"); without, the stream is a
    /// log and the events go after whatever is there. Notifies the channel on commit.
    /// </summary>
    internal async Task AppendAsync(
        string streamName,
        IReadOnlyCollection<IDomainEvent> events,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock($1)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue(EventTables.AppendLock);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        long current;
        await using (var revisionCommand = new NpgsqlCommand(
                         $"select coalesce(max(revision), -1) from {EventTables.Events} where stream = $1", connection, transaction))
        {
            revisionCommand.Parameters.AddWithValue(streamName);
            current = (long)(await revisionCommand.ExecuteScalarAsync(cancellationToken))!;
        }

        if (expectedRevision.HasValue && expectedRevision.Value != current)
            throw new ConcurrencyException(
                $"Stream {streamName} is at revision {current}, not the expected {expectedRevision.Value}; " +
                "it was modified by another writer since it was loaded.");

        var revision = current;
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            foreach (var @event in events)
            {
                var serialized = eventSerializer.SerializeEvent(@event);
                revision++;

                var insert = new NpgsqlBatchCommand(
                    $"insert into {EventTables.Events} (stream, revision, event_type, version, data, metadata, occurred_at) " +
                    "values ($1, $2, $3, $4, $5::json, $6::json, now())");
                insert.Parameters.AddWithValue(streamName);
                insert.Parameters.AddWithValue(revision);
                insert.Parameters.AddWithValue(serialized.EventType);
                insert.Parameters.AddWithValue(ReadVersion(serialized.Metadata));
                insert.Parameters.AddWithValue(Encoding.UTF8.GetString(serialized.Data));
                insert.Parameters.AddWithValue(Encoding.UTF8.GetString(serialized.Metadata));
                batch.BatchCommands.Add(insert);
            }

            var notify = new NpgsqlBatchCommand("select pg_notify($1, $2)");
            notify.Parameters.AddWithValue(EventTables.Channel);
            notify.Parameters.AddWithValue(streamName);
            batch.BatchCommands.Add(notify);

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    internal IDomainEvent Deserialize(string eventType, string data, string metadata) =>
        eventSerializer.DeserializeEvent(eventType, Encoding.UTF8.GetBytes(data), Encoding.UTF8.GetBytes(metadata));

    /// <summary>
    /// The version the serializer wrote into the metadata, copied into its own column so a
    /// query can ask "which events are still at v1" without parsing JSON. The serializer's own
    /// reading of the metadata is what deserialization uses; this column is for operators.
    /// </summary>
    private static int ReadVersion(byte[] metadata)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(metadata);
            return document.RootElement.TryGetProperty("v", out var v) && v.TryGetInt32(out var version) ? version : 1;
        }
        catch (System.Text.Json.JsonException)
        {
            return 1;
        }
    }
}
