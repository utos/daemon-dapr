using System.Runtime.CompilerServices;
using Common.SeedWork;
using Execution.Application.Exceptions;
using Execution.Domain.Services;
using Mediator;

namespace Execution.Application.Queries;

/// <summary>
/// Follows an execution's output stream from a cursor until its terminal entry.
/// <paramref name="After"/> and <paramref name="Tail"/> are mutually exclusive on the wire, so no
/// check is needed here.
/// </summary>
public record WatchExecutionOutputQuery(
    ExecutionId ExecutionId,
    long? After,
    long? Tail) : IStreamQuery<OutputEntryAt>;

/// <summary>
/// Reading is unprivileged: a client here observes, and never gates the execution producing the
/// entries, however far behind its cursor falls. Only a <c>workflow.call</c> parent that declared
/// <c>on_emitted</c> gates, and that path does not come through here.
/// </summary>
public class WatchExecutionOutputHandler(
    IEventStoreRepository eventStore,
    IExecutionOutputStore outputStore)
    : IStreamQueryHandler<WatchExecutionOutputQuery, OutputEntryAt>
{
    public async IAsyncEnumerable<OutputEntryAt> Handle(
        WatchExecutionOutputQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!await eventStore.ExistsAsync(StreamNames.Execution(query.ExecutionId), ct))
            throw new ExecutionNotFoundException(query.ExecutionId);

        var last = await outputStore.LastEntryAsync(query.ExecutionId, ct);

        var after = query.Tail is { } tail ? StartOfTail(last, tail) : query.After;

        // A cursor already at or past a terminal entry has nothing left to wait for: the stream
        // is closed and no entry can follow it. Without this the read would subscribe and hang on
        // a finished execution — the exact case a client resuming from its last cursor hits.
        if (last is { Entry.IsTerminal: true } && after >= last.Sequence)
            yield break;

        await foreach (var entry in outputStore.ReadAsync(query.ExecutionId, after, ct))
            yield return entry;
    }

    /// <summary>
    /// The cursor that leaves exactly <paramref name="tail"/> entries ahead of it, or null — read
    /// from the beginning — when the stream is not that long.
    /// </summary>
    private static long? StartOfTail(OutputEntryAt? last, long tail)
    {
        if (last is null || tail > last.Sequence)
            return null;

        return last.Sequence - tail;
    }
}
