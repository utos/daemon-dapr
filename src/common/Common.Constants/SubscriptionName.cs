namespace Common.Constants;

public static class SubscriptionName
{
    /// <summary>
    /// Suffixed rather than reused: a persistent subscription group stores its event-type filter
    /// server-side at creation, and the daemon's CreateToAll swallows AlreadyExists — so a group
    /// left over from before the events were renamed would keep the old filter and quietly match
    /// nothing. Bump this whenever the filter changes.
    /// </summary>
    public const string WorkflowEventProcessor = "workflow-event-processor-v2";
}
