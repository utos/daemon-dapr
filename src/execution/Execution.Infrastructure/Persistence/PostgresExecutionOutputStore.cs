using System.Runtime.CompilerServices;
using Common.SeedWork;
using Execution.Domain.Output;
using Execution.Domain.Services;
using Execution.Infrastructure.EventProcessing;

namespace Execution.Infrastructure.Persistence;

/// <summary>
/// An execution's output stream, as a stream in the event table.
/// <para>
/// The table already gives the two properties this needs: an append-only per-stream order, and
/// a revision that can be resumed from. So the sequence a caller cursors on <i>is</i> the
/// revision — no counter of our own to keep, and nothing that can disagree with what is on disk.
/// </para>
/// <para>
/// Appended without an expected revision. This is a log, not an aggregate: there is no invariant
/// spanning entries for an optimistic check to protect, and a producer retrying an append after
/// a lost acknowledgement should not fail on a conflict.
/// </para>
/// </summary>
public sealed class PostgresExecutionOutputStore(
    PostgresEventStore store,
    EventNotifications notifications) : IExecutionOutputStore
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public Task AppendAsync(
        ExecutionId executionId,
        ExecutionOutputEntry entry,
        CancellationToken cancellationToken = default) =>
        store.AppendAsync(StreamNames.ExecutionOutput(executionId), [entry], expectedRevision: null, cancellationToken);

    public async IAsyncEnumerable<OutputEntryAt> ReadAsync(
        ExecutionId executionId,
        long? afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streamName = StreamNames.ExecutionOutput(executionId);
        var last = afterSequence ?? -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (var (revision, eventType, data, metadata) in store.ReadStreamAfterAsync(streamName, last, cancellationToken))
            {
                last = revision;

                if (store.Deserialize(eventType, data, metadata) is not ExecutionOutputEntry entry)
                    continue;

                yield return new OutputEntryAt(revision, entry);

                // A stream ends after its terminal entry, so a reader that has caught up stops
                // rather than waiting for something that will never be appended.
                if (entry.IsTerminal)
                    yield break;
            }

            await notifications.WaitAsync(streamName, PollInterval, cancellationToken);
        }
    }

    public async Task<OutputEntryAt?> LastEntryAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        var row = await store.ReadLastAsync(StreamNames.ExecutionOutput(executionId), cancellationToken);
        if (row is not var (revision, eventType, data, metadata)) return null;

        return store.Deserialize(eventType, data, metadata) is ExecutionOutputEntry entry
            ? new OutputEntryAt(revision, entry)
            : null;
    }
}
