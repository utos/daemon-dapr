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

    /// <summary>
    /// How long a timer waits, parsed. The author wrote either the unit shorthand or a whole-field
    /// template; by the time it is here both have become one <see cref="TimeSpan"/>.
    /// <para>
    /// Resolved through this activity rather than read straight off the configuration because a
    /// template may read <c>now()</c> or the run's <c>env</c>, and an orchestrator must not render
    /// anything: rendering here records the result in history, so a replay reads the same deadline
    /// instead of computing a new one from a later clock.
    /// </para>
    /// </summary>
    public sealed record Timer(TimeSpan Duration) : ResolvedActivityInput;
}
