namespace IntegrationTests.Shared;

/// <summary>
/// Why a test is disabled, and what has to happen before it comes back. Kept in one place so the
/// answer to "can this be re-enabled yet" is a single edit rather than a search.
/// </summary>
public static class SkipReasons
{
    /// <summary>
    /// Dapr silently drops a termination that arrives in the same work item as another event —
    /// <c>[ExecutionTerminated, TaskCompleted]</c> rather than <c>[ExecutionTerminated]</c>. The
    /// workflow stays RUNNING forever, and because the event is consumed with the work item the
    /// terminate can never be re-delivered, so the instance is permanently unstoppable and the
    /// caller blocks until it gives up.
    /// <para>
    /// Not a daemon fault, and not something the daemon can recover from: fixed upstream in
    /// dapr/durabletask-go#124 and confirmed by the Dapr maintainers for runtime <b>1.18.3</b>.
    /// Measured at roughly 3% of cancellations of a running workflow on both 1.16.8 and 1.18.2,
    /// which is often enough to fail a suite run every few attempts.
    /// </para>
    /// <para>
    /// <b>Re-enable all tests carrying this reason once the daemon runs on Dapr 1.18.3 or later</b>,
    /// and delete this constant. Every one of them passes when the termination happens to arrive
    /// alone, so they need no change — only a runtime that keeps it.
    /// </para>
    /// </summary>
    public const string DaprDropsTerminationsSharingAWorkItem =
        "Dapr drops a termination delivered in the same work item as another event, leaving the "
        + "workflow running and unstoppable (dapr/durabletask-go#124). Fixed upstream, shipping in "
        + "Dapr 1.18.3 — re-enable then. See SkipReasons for the full account.";
}
