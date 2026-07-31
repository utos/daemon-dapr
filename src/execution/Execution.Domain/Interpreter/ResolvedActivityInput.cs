namespace Execution.Domain.Interpreter;

/// <summary>
/// An activity's configuration with every template in it rendered, ready for whatever
/// performs the actual work.
/// </summary>
public abstract record ResolvedActivityInput
{
    private ResolvedActivityInput()
    {
    }

    public sealed record Http(
        string Url,
        string Method,
        string? Body,
        Dictionary<string, string> Headers) : ResolvedActivityInput;

    /// <param name="Detached">
    /// True when the sub-workflow is fire-and-forget: it starts as an independent execution
    /// and is not awaited.
    /// </param>
    public sealed record SubWorkflow(
        string WorkflowKey,
        string StartActivity,
        Dictionary<string, object>? Input,
        bool Detached) : ResolvedActivityInput;
}
