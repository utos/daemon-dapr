using Common.SeedWork;
using Execution.Domain.Aggregates;
using Execution.Domain.Events;
using Execution.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Common.Workflow;

namespace UnitTests.Persistence;

public class ExecutionLifecycleWriterTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static WorkflowExecutionCreatedEvent CreatedEvent()
    {
        return new WorkflowExecutionCreatedEvent(
            Id,
            new WorkflowBundle("test:1.0.0", new Dictionary<string, Workflow>()),
            new Dictionary<string, object>(),
            "start",
            DateTime.UtcNow);
    }

    private static ExecutionLifecycleWriter Writer(FakeEventStore store)
    {
        return new ExecutionLifecycleWriter(store, NullLogger<ExecutionLifecycleWriter>.Instance);
    }

    [Fact]
    public async Task A_transition_is_loaded_decided_and_saved()
    {
        var store = new FakeEventStore([CreatedEvent()]);

        await Writer(store).ApplyAsync(Id, execution => execution.MarkActive());

        Assert.Equal(1, store.Loads);
        var saved = Assert.Single(store.Saved);
        Assert.IsType<WorkflowExecutionActiveEvent>(saved);
    }

    [Fact]
    public async Task A_transition_that_changes_nothing_is_never_written()
    {
        // Saving an aggregate with no uncommitted events would be a wasted round trip, and
        // would take a version on a stream this writer has no business touching.
        var store = new FakeEventStore([CreatedEvent(), new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow)]);

        await Writer(store).ApplyAsync(Id, execution => execution.MarkActive());

        Assert.Empty(store.Saved);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task Losing_a_race_reloads_and_decides_again()
    {
        // The loser's decision was made against stale state, so it must be reconsidered — not
        // retried as-is.
        var store = new FakeEventStore([CreatedEvent()]) { FailSaves = 1 };

        await Writer(store).ApplyAsync(Id, execution => execution.MarkActive());

        Assert.Equal(2, store.Loads);
        Assert.Equal(2, store.SaveAttempts);
        Assert.IsType<WorkflowExecutionActiveEvent>(Assert.Single(store.Saved));
    }

    [Fact]
    public async Task Losing_a_race_to_a_writer_that_already_did_the_work_settles_quietly()
    {
        // The winner applied the same transition, so on reload this one becomes a no-op rather
        // than an error — which is the whole point of reloading instead of retrying.
        var store = new FakeEventStore([CreatedEvent()]) { FailSaves = 1 };
        store.OnConflictAppend = new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow);

        await Writer(store).ApplyAsync(Id, execution => execution.MarkActive());

        Assert.Empty(store.Saved);
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task A_persistent_conflict_eventually_surfaces()
    {
        // Retrying forever would hide a genuine problem behind a hung activity.
        var store = new FakeEventStore([CreatedEvent()]) { FailSaves = int.MaxValue };

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => Writer(store).ApplyAsync(Id, execution => execution.MarkActive()));

        Assert.Equal(3, store.SaveAttempts);
    }

    [Fact]
    public async Task The_failure_reason_reaches_the_written_event()
    {
        var store = new FakeEventStore([CreatedEvent()]);

        await Writer(store).ApplyAsync(Id, execution => execution.Fail("HTTP 500"));

        var failed = Assert.IsType<WorkflowExecutionFailedEvent>(Assert.Single(store.Saved));
        Assert.Equal("HTTP 500", failed.Reason);
    }

    /// <summary>
    /// An in-memory stand-in for the event store. Only the two members the writer uses do
    /// anything; the rest exist to satisfy the interface.
    /// </summary>
    private sealed class FakeEventStore(List<IDomainEvent> history) : IEventStoreRepository
    {
        public int Loads { get; private set; }
        public int SaveAttempts { get; private set; }
        public List<IDomainEvent> Saved { get; } = [];

        /// <summary>How many save attempts to reject as conflicts before letting one through.</summary>
        public int FailSaves { get; set; }

        /// <summary>Appended to the history when a save is rejected, as the winning writer would.</summary>
        public IDomainEvent? OnConflictAppend { get; set; }

        public Task<T> LoadAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull
        {
            Loads++;

            var aggregate = new T();
            aggregate.LoadFromHistory(history, history.Count - 1);

            return Task.FromResult(aggregate);
        }

        public Task SaveAsync<T, TId>(T aggregate)
            where T : AggregateRoot<TId>, new() where TId : notnull
        {
            SaveAttempts++;

            if (FailSaves > 0)
            {
                FailSaves--;

                if (OnConflictAppend != null)
                    history.Add(OnConflictAppend);

                throw new ConcurrencyException($"Aggregate {aggregate.Id} was modified by another writer.");
            }

            var appended = aggregate.GetDomainEvents().ToList();
            Saved.AddRange(appended);
            history.AddRange(appended);
            aggregate.MarkCommitted(appended.Count);
            aggregate.ClearDomainEvents();

            return Task.CompletedTask;
        }

        public Task AppendEventAsync<T, TId>(TId aggregateId, IDomainEvent @event)
            where T : AggregateRoot<TId>, new() where TId : notnull
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull
            => throw new NotSupportedException();

        public Task AppendToStreamAsync(string streamName, IDomainEvent @event)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string streamName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteStreamAsync(string streamName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<EventWithNumber> SubscribeToStream<T, TId>(TId aggregateId, long? afterPosition,
            CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull
            => throw new NotSupportedException();
    }
}
