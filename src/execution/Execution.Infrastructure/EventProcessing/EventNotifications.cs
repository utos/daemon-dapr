using System.Collections.Concurrent;
using Execution.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Execution.Infrastructure.EventProcessing;

/// <summary>
/// The wake-up behind every wait on the event table. One connection holds <c>LISTEN</c> on the
/// events channel; every append <c>NOTIFY</c>s it with the stream name; waiters — the
/// subscription processor tailing <c>$all</c>, an output-stream reader parked on its stream —
/// are released in memory.
/// <para>
/// A hint, never the truth. Every waiter also polls on a fallback interval, so a notification
/// lost to a dropped connection costs latency, not correctness; and the listener reconnects on
/// its own. That is what makes it safe to build on: the table is the record, this is only how
/// fast readers find out it changed.
/// </para>
/// </summary>
public sealed class EventNotifications(
    NpgsqlDataSource dataSource,
    ILogger<EventNotifications> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.Ordinal);

    /// <summary>The key a waiter uses to be woken by any append, whatever the stream.</summary>
    public const string Any = "*";

    /// <summary>
    /// Waits until <paramref name="key"/> — a stream name, or <see cref="Any"/> — is notified, or
    /// <paramref name="timeout"/> passes. Returns normally either way: the caller re-reads the
    /// table regardless, which is what makes a missed notification harmless.
    /// </summary>
    public async Task WaitAsync(string key, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = _signals.GetOrAdd(key, _ => new SemaphoreSlim(0, 1));
        try
        {
            await signal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller's token: it stops waiting, and will observe the cancellation itself.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "The event notification listener lost its connection; waiters fall back to polling until it returns");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);

        connection.Notification += (_, notification) =>
        {
            Release(notification.Payload);
            Release(Any);
        };

        await using (var listen = new NpgsqlCommand($"LISTEN {EventTables.Channel}", connection))
            await listen.ExecuteNonQueryAsync(stoppingToken);

        logger.LogDebug("Listening for event notifications on '{Channel}'", EventTables.Channel);

        while (!stoppingToken.IsCancellationRequested)
            await connection.WaitAsync(stoppingToken);
    }

    private void Release(string key)
    {
        // Capped at one pending release: a waiter that comes back re-reads the table, so ten
        // notifications while it was busy are worth exactly one wake-up.
        if (_signals.TryGetValue(key, out var signal) && signal.CurrentCount == 0)
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another release; one pending wake-up is already there.
            }
        }
    }

    public override void Dispose()
    {
        foreach (var signal in _signals.Values) signal.Dispose();
        base.Dispose();
    }
}
