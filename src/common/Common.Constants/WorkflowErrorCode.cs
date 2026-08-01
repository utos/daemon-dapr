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
}
