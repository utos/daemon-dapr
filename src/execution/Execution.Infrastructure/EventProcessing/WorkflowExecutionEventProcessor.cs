using Common.Constants;
using Common.Infrastructure.EventHandling;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Projections;
using Execution.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Execution.Infrastructure.EventProcessing;

/// <summary>
/// Feeds every <c>utos.execution.*</c> event, in global order, to the projections and the event
/// handlers — the read model and the lifecycle notifier.
/// <para>
/// A tail on the event table by position, from a durable checkpoint, woken by
/// <see cref="EventNotifications"/> and polling regardless. It replaces a KurrentDB persistent
/// subscription with a server-side prefix filter; the filter is a <c>like</c> on the type name
/// here, and <c>EventTypeRegistryTests</c> still pins that output entries are named outside the
/// prefix so they never reach this. An event that fails to process is <em>parked</em> — recorded
/// in <c>parked_events</c> and moved past — which is what the persistent subscription did too,
/// except that the queue is now a table an operator can read.
/// </para>
/// </summary>
public class WorkflowExecutionEventProcessor(
    NpgsqlDataSource dataSource,
    IServiceProvider serviceProvider,
    IEventSerializer eventSerializer,
    EventNotifications notifications,
    ILogger<WorkflowExecutionEventProcessor> logger)
    : BackgroundService
{
    private const string GroupName = SubscriptionName.WorkflowEventProcessor;
    private const string Prefix = "utos.execution.";
    private const int BatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private sealed record Row(long Position, string Stream, string EventType, string Data, string Metadata);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting WorkflowExecution event subscription service");

        var checkpoint = await EnsureCheckpointAsync(cancellationToken);
        logger.LogInformation("Subscription '{GroupName}' resuming after position {Position}", GroupName, checkpoint);

        while (!cancellationToken.IsCancellationRequested)
        {
            List<Row> batch;
            try
            {
                batch = await ReadBatchAsync(checkpoint, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading the event table failed; retrying");
                await Task.Delay(PollInterval, cancellationToken);
                continue;
            }

            foreach (var row in batch)
            {
                try
                {
                    await ProcessAsync(row, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing event {EventType} at position {Position}; parking it",
                        row.EventType, row.Position);
                    await ParkAsync(row, ex, cancellationToken);
                }

                checkpoint = row.Position;
                await AdvanceCheckpointAsync(checkpoint, cancellationToken);
            }

            // A full batch means there is probably more; an empty or partial one means wait.
            if (batch.Count < BatchSize)
                await notifications.WaitAsync(EventNotifications.Any, PollInterval, cancellationToken);
        }
    }

    private async Task ProcessAsync(Row row, CancellationToken cancellationToken)
    {
        // Deliberately unguarded. An event that cannot be read throws, and the caller parks it —
        // where it can be inspected — rather than skipping it, which is what returning early
        // did: the projection never ran and the read model diverged in silence.
        var domainEvent = eventSerializer.DeserializeEvent(
            row.EventType,
            System.Text.Encoding.UTF8.GetBytes(row.Data),
            System.Text.Encoding.UTF8.GetBytes(row.Metadata));

        using var scope = serviceProvider.CreateScope();

        // Projections: idempotent read model updates (safe to replay)
        var projectionEngine = scope.ServiceProvider.GetRequiredService<IProjectionEngine>();
        await projectionEngine.ProcessEventAsync(domainEvent, row.Position, cancellationToken);

        // Event handlers: side-effect-producing reactions (not safe to replay)
        var eventHandlers = scope.ServiceProvider.GetRequiredService<IEnumerable<IEventHandler>>();
        var domainEventType = domainEvent.GetType();
        foreach (var handler in eventHandlers)
        {
            if (!handler.CanHandle(domainEventType))
                continue;

            try
            {
                await handler.HandleAsync(domainEvent, row.Position, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Event handler {HandlerType} failed to handle event {EventType} at position {Position}",
                    handler.GetType().Name, domainEventType.Name, row.Position);
                throw;
            }
        }
    }

    private async Task<long> EnsureCheckpointAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"insert into {EventTables.Checkpoints} (name, position, updated_at) values ($1, 0, now()) " +
            "on conflict (name) do nothing returning position", connection);
        command.Parameters.AddWithValue(GroupName);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var read = new NpgsqlCommand($"select position from {EventTables.Checkpoints} where name = $1", connection);
        read.Parameters.AddWithValue(GroupName);
        return (long)(await read.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<List<Row>> ReadBatchAsync(long afterPosition, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select position, stream, event_type, data, metadata from {EventTables.Events} " +
            "where position > $1 and event_type like $2 order by position limit $3", connection);
        command.Parameters.AddWithValue(afterPosition);
        command.Parameters.AddWithValue(Prefix + "%");
        command.Parameters.AddWithValue(BatchSize);

        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new Row(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    private async Task AdvanceCheckpointAsync(long position, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"update {EventTables.Checkpoints} set position = $2, updated_at = now() where name = $1", connection);
        command.Parameters.AddWithValue(GroupName);
        command.Parameters.AddWithValue(position);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ParkAsync(Row row, Exception error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"insert into {EventTables.Parked} (position, subscription, stream, event_type, error, parked_at) " +
            "values ($1, $2, $3, $4, $5, now()) on conflict (position, subscription) do nothing", connection);
        command.Parameters.AddWithValue(row.Position);
        command.Parameters.AddWithValue(GroupName);
        command.Parameters.AddWithValue(row.Stream);
        command.Parameters.AddWithValue(row.EventType);
        command.Parameters.AddWithValue(error.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
