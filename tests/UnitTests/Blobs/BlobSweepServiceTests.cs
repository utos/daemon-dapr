using Blobs.Application;
using Blobs.Infrastructure.Store;
using Common.Blobs;
using Common.SeedWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UnitTests.Blobs;

/// <summary>
/// The loop that runs the retention sweep, and the two things about it that are not obvious.
/// <para>
/// A sweep is housekeeping. If a pass throws — a database briefly unavailable, an object store
/// throttling — the loop has to survive it, because a loop that has stopped never reports anything
/// again and the next symptom is not an error but a full bucket. And it opens a scope per pass, since
/// the repository's <c>DbContext</c> is scoped and one held for the life of the process would
/// accumulate every entity it ever tracked.
/// </para>
/// </summary>
public class BlobSweepServiceTests
{
    [Fact]
    public async Task A_failing_pass_does_not_stop_the_loop()
    {
        var log = new SweepLog { FailFirst = 2 };
        using var service = Service(log, TimeSpan.FromMilliseconds(20));

        await service.StartAsync(CancellationToken.None);
        await log.Reached(3);
        await service.StopAsync(CancellationToken.None);

        // Three passes means the first two threw and the loop came back anyway.
        Assert.True(log.Passes >= 3, $"only {log.Passes} passes happened");
    }

    [Fact]
    public async Task Every_pass_gets_its_own_scope()
    {
        var log = new SweepLog();
        using var service = Service(log, TimeSpan.FromMilliseconds(20));

        await service.StartAsync(CancellationToken.None);
        await log.Reached(3);
        await service.StopAsync(CancellationToken.None);

        // The scopes are the container's own, resolved per pass. A service that opened one scope and
        // reused it would show the same marker every time — and would hold one DbContext for the life
        // of the process, tracking every entity it ever loaded.
        Assert.Equal(log.Scopes.Count, log.Scopes.Distinct().Count());
    }

    [Fact]
    public async Task A_non_positive_interval_turns_the_sweep_off_rather_than_spinning()
    {
        // A supported deployment, and one worth being able to choose — but the service says so in a
        // warning, because a deployment that turned the sweep off and forgot is a bucket that grows
        // without anyone having decided that it should.
        var log = new SweepLog();
        using var service = Service(log, TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, log.Passes);
    }

    [Fact]
    public async Task The_first_pass_waits_an_interval_rather_than_running_at_startup()
    {
        // Nothing here is urgent — every retention window is measured in hours at least — and a daemon
        // coming up under load has better things to do than scan a table.
        var log = new SweepLog();
        using var service = Service(log, TimeSpan.FromMinutes(5));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, log.Passes);
    }

    private static BlobSweepService Service(SweepLog log, TimeSpan interval)
    {
        var services = new ServiceCollection();

        // Scoped, so each resolution inside a scope is that scope's own instance — which is what makes
        // "one scope per pass" observable rather than something the fake asserts about itself.
        services.AddScoped<ScopeMarker>();
        services.AddScoped<IBlobReclamation>(provider =>
            new RecordingReclaimer(log, provider.GetRequiredService<ScopeMarker>()));

        var provider = services.BuildServiceProvider();

        return new BlobSweepService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OptionsWrapper<BlobOptions>(new BlobOptions { SweepInterval = interval }),
            NullLogger<BlobSweepService>.Instance);
    }

    /// <summary>One instance per container scope, so identity says which scope a pass ran in.</summary>
    private sealed class ScopeMarker;

    /// <summary>What the passes did, shared across the scopes they ran in.</summary>
    private sealed class SweepLog
    {
        private readonly TaskCompletionSource _enough =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _target = int.MaxValue;

        public int Passes { get; private set; }

        public List<ScopeMarker> Scopes { get; } = [];

        /// <summary>How many of the first passes throw, so the loop's recovery is observable.</summary>
        public int FailFirst { get; init; }

        public bool Record(ScopeMarker scope)
        {
            lock (Scopes)
            {
                Passes++;
                Scopes.Add(scope);

                if (Passes >= _target) _enough.TrySetResult();

                return Passes <= FailFirst;
            }
        }

        /// <summary>Waits for <paramref name="passes"/> passes rather than for a duration.</summary>
        public Task Reached(int passes)
        {
            lock (Scopes)
            {
                _target = passes;
                if (Passes >= passes) _enough.TrySetResult();
            }

            return _enough.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class RecordingReclaimer(SweepLog log, ScopeMarker scope) : IBlobReclamation
    {
        public Task<BlobReclamationReport> SweepAsync(CancellationToken cancellationToken)
        {
            if (log.Record(scope))
                throw new InvalidOperationException("the database was briefly unavailable");

            return Task.FromResult(BlobReclamationReport.Empty);
        }

        public Task<BlobReclamationReport> ReclaimRunTreeAsync(
            ExecutionId rootExecution, bool deleteDurable, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
