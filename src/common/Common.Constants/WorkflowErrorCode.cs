namespace Common.Constants;

/// <summary>
/// Codes carried on <c>utos.workflow.v1.WorkflowError</c>. Shared rather than mirrored: the query
/// response and the watch stream report the same failure, and a client switching on the code is
/// looking at one run through two APIs.
/// </summary>
public static class WorkflowErrorCode
{
    /// <summary>
    /// Coarse code for "the run failed", until the execution pipeline records structured failure
    /// causes of its own.
    /// </summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>
    /// The run was stopped on purpose. Distinct from <see cref="ExecutionFailed"/> everywhere the
    /// two could be confused — the terminal entry of a cancelled execution's output stream is one
    /// such place, since a reader sees only the entry and not the run's status.
    /// </summary>
    public const string ExecutionCancelled = "EXECUTION_CANCELLED";
}
