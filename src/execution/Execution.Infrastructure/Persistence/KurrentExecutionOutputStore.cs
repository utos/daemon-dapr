using System.Runtime.CompilerServices;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Extensions;
using Common.SeedWork;
using Execution.Domain.Output;
using Execution.Domain.Services;
using KurrentDB.Client;

namespace Execution.Infrastructure.Persistence;

/// <summary>
/// An execution's output stream, over its own KurrentDB stream.
/// <para>
/// The engine already gives the two properties this needs: an append-only per-stream order, and
/// a revision that can be resumed from. So the sequence a caller cursors on *is* the stream
/// revision — no counter of our own to keep, and nothing that can disagree with what is on disk.
/// </para>
/// <para>
/// Appended with <see cref="StreamState.Any"/> rather than an expected revision. This is a log,
/// not an aggregate: there is no invariant spanning entries for an optimistic check to protect,
/// and a producer retrying an append after a lost acknowledgement should not fail on a conflict.
/// </para>
/// </summary>
public sealed class KurrentExecutionOutputStore(
    KurrentDBClient client,
    IEventSerializer eventSerializer) : IExecutionOutputStore
{
    public async Task AppendAsync(
        ExecutionId executionId,
        ExecutionOutputEntry entry,
        CancellationToken cancellationToken = default)
    {
        var serialized = eventSerializer.SerializeEvent(entry);

        await client.AppendToStreamAsync(
            StreamNames.ExecutionOutput(executionId),
            StreamState.Any,
            [new EventData(Uuid.NewUuid(), serialized.EventType, serialized.Data, serialized.Metadata)],
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<OutputEntryAt> ReadAsync(
        ExecutionId executionId,
        long? afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fromStream = afterSequence.HasValue
            ? FromStream.After(StreamPosition.FromInt64(afterSequence.Value))
            : FromStream.Start;

        await using var subscription = client.SubscribeToStream(
            StreamNames.ExecutionOutput(executionId),
            fromStream,
            cancellationToken: cancellationToken);

        await foreach (var message in subscription.Messages.WithCancellation(cancellationToken))
        {
            if (message is not StreamMessage.Event @event)
                continue;

            var deserialized = eventSerializer.DeserializeEvent(
                @event.GetEventType(), @event.GetEventData(), @event.GetMetadata());

            if (deserialized is not ExecutionOutputEntry entry)
                continue;

            yield return new OutputEntryAt(@event.GetEventNumber(), entry);

            // A stream ends after its terminal entry, so a reader that has caught up stops
            // rather than hanging on a subscription that will never produce anything else.
            if (entry.IsTerminal)
                yield break;
        }
    }

    public async Task<IReadOnlyList<OutputEntryAt>> SnapshotAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<OutputEntryAt>();

        var stream = client.ReadStreamAsync(
            Direction.Forwards,
            StreamNames.ExecutionOutput(executionId),
            StreamPosition.Start,
            cancellationToken: cancellationToken);

        if (await stream.ReadState == ReadState.StreamNotFound)
            return entries;

        await foreach (var resolved in stream.WithCancellation(cancellationToken))
        {
            var deserialized = eventSerializer.DeserializeEvent(
                resolved.Event.EventType, resolved.Event.Data.ToArray(), resolved.Event.Metadata.ToArray());

            if (deserialized is ExecutionOutputEntry entry)
                entries.Add(new OutputEntryAt(resolved.Event.EventNumber.ToInt64(), entry));
        }

        return entries;
    }
}
