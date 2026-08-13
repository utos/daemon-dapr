using Common.SeedWork;
using Execution.Domain.Output;

namespace Execution.Domain.Services;

/// <summary>One entry, with the position that doubles as a cursor.</summary>
public sealed record OutputEntryAt(long Sequence, ExecutionOutputEntry Entry);

/// <summary>
/// An execution's durable output stream. Ordered, append-only, addressed by cursor.
/// <para>
/// "Resume after N" has to be a pure function of recorded history — that is what makes replay,
/// reconnection and a daemon restart the same operation, and what lets a consumer hold a number
/// rather than a live subscription.
/// </para>
/// </summary>
public interface IExecutionOutputStore
{
    /// <summary>
    /// Appends an entry. The sequence it lands at is not returned: readers take it from the
    /// stream itself, so there is no second copy to disagree with what is on disk.
    /// </summary>
    Task AppendAsync(
        ExecutionId executionId,
        ExecutionOutputEntry entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads forward from <paramref name="afterSequence"/> (exclusive; null starts at the
    /// beginning), yielding entries as they arrive and completing after the terminal one.
    /// </summary>
    IAsyncEnumerable<OutputEntryAt> ReadAsync(
        ExecutionId executionId,
        long? afterSequence,
        CancellationToken cancellationToken = default);

    /// <summary>Every entry recorded so far, for callers that need the whole thing at once.</summary>
    Task<IReadOnlyList<OutputEntryAt>> SnapshotAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The last entry recorded, or null on an empty stream. Two questions a reader has to answer
    /// before it can start — where "the last N entries" begins, and whether the stream has already
    /// terminated — and both are answerable from one entry, without dragging the whole stream back
    /// to count it.
    /// </summary>
    Task<OutputEntryAt?> LastEntryAsync(
        ExecutionId executionId,
        CancellationToken cancellationToken = default);
}
