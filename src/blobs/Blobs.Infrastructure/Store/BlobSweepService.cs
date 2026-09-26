using Blobs.Application;
using Common.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blobs.Infrastructure.Store;

/// <summary>
/// Runs the sweep on an interval — <c>api/docs/binary-data.md</c> § When intermediates go, third
/// bullet.
/// <para>
/// The spec allows an intermediate to go the moment every tree it belongs to has completed, and this
/// deliberately does not do that inline. A run's terminal event is delivered on a subscription whose
/// await chain must not include an object store, so what the ending does is record itself; what
/// deletes bytes is a timer. "At once" in the spec is a permission, not an obligation — the
/// requirement is only ever the other direction, that nothing goes too early.
/// </para>
/// </summary>
public sealed class BlobSweepService(
    IServiceScopeFactory scopes,
    IOptions<BlobOptions> options,
    ILogger<BlobSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;

        if (interval <= TimeSpan.Zero)
        {
            // Said out loud. A deployment that turned the sweep off and forgot is a bucket that grows
            // without anyone deciding that it should, and the symptom arrives as a bill.
            logger.LogWarning(
                "The blob retention sweep is disabled (Blobs:SweepInterval is {SweepInterval}); "
                + "intermediate blobs will not be removed", interval);
            return;
        }

        using var timer = new PeriodicTimer(interval);

        // The first pass waits one interval rather than running at startup. Nothing is urgent — every
        // window here is measured in hours at least — and a daemon coming up under load has better
        // things to do than scan a table.
        while (await SafeWaitAsync(timer, stoppingToken))
            await SweepOnceAsync(stoppingToken);
    }

    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            // A scope per pass: the repository's DbContext is scoped, and a context held for the
            // lifetime of the process would accumulate every entity it ever tracked.
            using var scope = scopes.CreateScope();

            await scope.ServiceProvider
                .GetRequiredService<IBlobReclamation>()
                .SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception failure)
        {
            // Swallowed on purpose, and the only place in this module that does. A sweep is
            // housekeeping: a database that is briefly unavailable must not stop the loop, because a
            // loop that has stopped never reports anything again and the next symptom is a full
            // bucket rather than an error.
            logger.LogError(failure, "The blob retention sweep failed; it will run again in {SweepInterval}",
                options.Value.SweepInterval);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
