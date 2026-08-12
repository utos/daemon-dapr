namespace Execution.Domain.Aggregates;

/// <summary>
/// Lifecycle state of an execution.
///
/// The daemon's own enum rather than the wire one: this is durable aggregate state, replayed
/// from the event stream, and it should not be able to shift underneath the daemon because a
/// transport contract was renumbered. The gRPC edge maps it outward.
/// </summary>
public enum ExecutionStatus
{
    /// <summary>Created, but not yet accepted for execution.</summary>
    Unspecified = 0,

    Scheduled = 1,
    Active = 2,

    /// <summary>Terminal: success.</summary>
    Completed = 3,

    /// <summary>Terminal: failure.</summary>
    Failed = 4,

    /// <summary>
    /// Terminal: stopped on purpose, by a caller or by the daemon itself. Distinct from
    /// <see cref="Failed"/> because nothing went wrong — a poller cancelled deliberately has not
    /// failed, and collapsing the two would make every "why did this stop" answer ambiguous.
    /// </summary>
    Cancelled = 5
}
