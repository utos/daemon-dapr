namespace Execution.Infrastructure.Dapr;

/// <summary>
/// A run failed for a reason a workflow gave it: a rule's <c>error</c> action, evaluated here or
/// in a sub-workflow whose failure this run is now handling.
/// <para>
/// Distinct from every other exception the driver sees because it carries a <see cref="Code"/>
/// and <see cref="Details"/> that must survive to wherever the failure is reported — the output
/// stream's terminal entry, the lifecycle event, the message a consuming caller receives, and the
/// <c>error</c> scope of a caller's <c>on_failure</c> rule. A failure of the daemon's own is any
/// other exception, and reports the generic code.
/// </para>
/// </summary>
public sealed class WorkflowFailedException(
    string code,
    string message,
    Dictionary<string, object>? details)
    : Exception(message.Length == 0 ? code : message)
{
    public string Code { get; } = code;

    public Dictionary<string, object>? Details { get; } = details;
}
