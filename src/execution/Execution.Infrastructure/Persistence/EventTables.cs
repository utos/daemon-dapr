namespace Execution.Infrastructure.Persistence;

/// <summary>
/// The three tables the event store lives in, named once. EF Core owns their schema through
/// <c>WorkflowExecutionDbContext</c> (so one migration path creates everything); the stores
/// read and write them with Npgsql directly, because an append needs a transaction-scoped lock
/// and a reader may stay open for the life of a subscription — neither is a job for a scoped
/// <c>DbContext</c>.
/// </summary>
internal static class EventTables
{
    /// <summary>One row per event: a global position, a per-stream revision, and the serializer's output verbatim.</summary>
    public const string Events = "events";

    /// <summary>Where each subscriber group has read to, by global position.</summary>
    public const string Checkpoints = "subscription_checkpoints";

    /// <summary>Events a subscriber could not process and moved past — inspectable, unlike a parked queue behind a UI.</summary>
    public const string Parked = "parked_events";

    /// <summary>
    /// The channel every append notifies, with the stream name as payload. One channel rather
    /// than one per stream: a listener holds one connection and fans out in memory.
    /// </summary>
    public const string Channel = "utos_events";

    /// <summary>
    /// The advisory lock every append takes for its transaction. Global, deliberately: it makes
    /// global position order equal commit order, which is what lets a subscriber tail the table
    /// by position without ever skipping an event whose transaction committed late. Appends
    /// are milliseconds and serialising them is a cost this daemon can pay; a lock per stream
    /// would keep revisions correct but not positions.
    /// </summary>
    public const long AppendLock = 7005;
}
