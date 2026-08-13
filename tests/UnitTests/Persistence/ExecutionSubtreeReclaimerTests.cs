using Common.SeedWork;
using Execution.Application.Services;
using Execution.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace UnitTests.Persistence;

/// <summary>
/// A <c>workflow.call</c> child has no aggregate stream and no read-model row, so its context row
/// is the only record anywhere that it ran. That makes the order of these two deletes the whole
/// design: drop the rows first and the streams they name are not deleted, they are
/// <em>unreachable</em> — nothing left in the system can even enumerate them.
/// </summary>
public class ExecutionSubtreeReclaimerTests
{
    private static readonly ExecutionId Root = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ExecutionId Child = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ExecutionId Grandchild = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static ExecutionSubtreeReclaimer Reclaimer(FakeContextStore contexts, FakeEventStore streams) =>
        new(contexts, streams, NullLogger<ExecutionSubtreeReclaimer>.Instance);

    [Fact]
    public async Task Every_descendants_output_stream_is_deleted()
    {
        var contexts = new FakeContextStore([Child, Grandchild]);
        var streams = new FakeEventStore();

        await Reclaimer(contexts, streams).ReclaimAsync(Root);

        Assert.Equal(
            [StreamNames.ExecutionOutput(Child), StreamNames.ExecutionOutput(Grandchild)],
            streams.Deleted);
    }

    [Fact]
    public async Task The_streams_go_before_the_rows_that_name_them()
    {
        // Inverted, this silently leaks: the deletes would still be issued in a later release by
        // someone who reordered these two lines for tidiness, and nothing would fail.
        var contexts = new FakeContextStore([Child]);
        var streams = new FakeEventStore();

        var order = new List<string>();
        streams.OnDelete = _ => order.Add("stream");
        contexts.OnDeleteAll = () => order.Add("contexts");

        await Reclaimer(contexts, streams).ReclaimAsync(Root);

        Assert.Equal(["stream", "contexts"], order);
    }

    [Fact]
    public async Task The_runs_own_contexts_are_dropped_even_with_no_sub_workflows()
    {
        var contexts = new FakeContextStore([]);
        var streams = new FakeEventStore();

        await Reclaimer(contexts, streams).ReclaimAsync(Root);

        Assert.Empty(streams.Deleted);
        Assert.Equal(Root, contexts.DeletedRoot);
    }

    [Fact]
    public async Task Descendants_are_looked_up_from_the_root_being_reclaimed()
    {
        var contexts = new FakeContextStore([Child]);

        await Reclaimer(contexts, new FakeEventStore()).ReclaimAsync(Root);

        Assert.Equal(Root, contexts.ListedRoot);
    }

    private sealed class FakeContextStore(List<ExecutionId> descendants) : IExecutionContextStore
    {
        public ExecutionId? ListedRoot { get; private set; }
        public ExecutionId? DeletedRoot { get; private set; }
        public Action? OnDeleteAll { get; set; }

        public Task<IReadOnlyList<ExecutionId>> ListDescendantsAsync(
            ExecutionId rootExecutionId, CancellationToken ct = default)
        {
            ListedRoot = rootExecutionId;
            return Task.FromResult<IReadOnlyList<ExecutionId>>(descendants);
        }

        public Task DeleteAllAsync(ExecutionId rootExecutionId, CancellationToken ct = default)
        {
            OnDeleteAll?.Invoke();
            DeletedRoot = rootExecutionId;
            return Task.CompletedTask;
        }

        public Task SaveContextAsync(ExecutionContext context, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ExecutionContext> LoadContextAsync(ExecutionId executionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Only <c>DeleteStreamAsync</c> does anything; the rest satisfy the interface.</summary>
    private sealed class FakeEventStore : IEventStoreRepository
    {
        public List<string> Deleted { get; } = [];
        public Action<string>? OnDelete { get; set; }

        public Task DeleteStreamAsync(string streamName, CancellationToken cancellationToken = default)
        {
            OnDelete?.Invoke(streamName);
            Deleted.Add(streamName);
            return Task.CompletedTask;
        }

        public Task<T> LoadAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull => throw new NotSupportedException();

        public Task SaveAsync<T, TId>(T aggregate)
            where T : AggregateRoot<TId>, new() where TId : notnull => throw new NotSupportedException();

        public Task AppendEventAsync<T, TId>(TId aggregateId, IDomainEvent @event)
            where T : AggregateRoot<TId>, new() where TId : notnull => throw new NotSupportedException();

        public Task<bool> ExistsAsync<T, TId>(TId aggregateId, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull => throw new NotSupportedException();

        public Task AppendToStreamAsync(string streamName, IDomainEvent @event) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string streamName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<EventWithNumber> SubscribeToStream<T, TId>(
            TId aggregateId, long? afterPosition, CancellationToken cancellationToken = default)
            where T : AggregateRoot<TId>, new() where TId : notnull => throw new NotSupportedException();
    }
}
