namespace Common.Infrastructure.Logging;

/// <summary>
/// Structured log property names that route an entry into an execution's watch stream.
/// <para>
/// These are a contract between two modules that do not reference each other: execution writes
/// them and observability reads them. A spelling that differs on one side does not fail anywhere —
/// the entry simply never reaches a stream, or arrives unattributed — which is why the names live
/// once, here, rather than as a string literal at each end.
/// </para>
/// </summary>
public static class LogProperty
{
    /// <summary>The execution whose watch stream the entry belongs to — the root of the call tree.</summary>
    public const string WorkflowExecutionId = "WorkflowExecutionId";

    /// <summary>The workflow that wrote the entry, when it is not the root being watched.</summary>
    public const string WorkflowSource = "WorkflowSource";
}
