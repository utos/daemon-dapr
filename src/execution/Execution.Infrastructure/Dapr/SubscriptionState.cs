using Common.SeedWork;

namespace Execution.Infrastructure.Dapr;

/// <summary>
/// A caller's place in a sub-workflow's output stream, for a <c>workflow.call</c> activity that
/// declared <c>on_emitted</c>.
/// </summary>
/// <param name="ChildExecutionId">
/// Which run is producing. Needed to acknowledge each value back to it, and to cancel it if the
/// subscription is abandoned.
/// </param>
/// <param name="Cursor">
/// The sequence of the last entry consumed; <c>-1</c> before the first. This is what a
/// continuation resumes from — the value itself arrives as an external event, but an event
/// received and not yet handled when the segment restarts is only recoverable by re-reading the
/// stream from here.
/// </param>
public sealed record SubscriptionState(ExecutionId ChildExecutionId, long Cursor)
{
    /// <summary>Nothing consumed yet, so a resume re-reads from the start of the stream.</summary>
    public const long BeforeFirst = -1;
}
