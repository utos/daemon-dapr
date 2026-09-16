namespace Common.Workflow;

/// <summary>
/// A workflow document, as authored and as the daemon executes it. The domain's own shape —
/// the wire format is mapped into this at the transport edge and never reaches here.
/// </summary>
public sealed record Workflow(
    string ApiVersion,
    string Kind,
    WorkflowMetadata Metadata,
    WorkflowSpec Spec);

/// <summary>Identity of a workflow document.</summary>
public sealed record WorkflowMetadata(
    string Name,
    string Version,
    string? Namespace = null,
    string? Registry = null,
    string? Description = null);

/// <summary>
/// The executable body: a named graph of activities.
/// </summary>
/// <param name="Dependencies">
/// Source-format alias-to-reference declarations. The daemon never resolves these — a built
/// bundle names dependencies by canonical identity — but they are carried so a bundle read back
/// through <c>GetWorkflow</c> or <c>GetExecutionDetails</c> is identical to the one loaded.
/// </param>
public sealed record WorkflowSpec(
    IReadOnlyDictionary<string, WorkflowActivity> Activities,
    IReadOnlyDictionary<string, string>? Dependencies = null)
{
    public static WorkflowSpec Empty { get; } = new(new Dictionary<string, WorkflowActivity>());

    public IReadOnlyDictionary<string, string> DependencyMap =>
        Dependencies ?? new Dictionary<string, string>();
}

/// <summary>
/// A fully-resolved set of workflows plus the one execution starts from. Every dependency a
/// workflow references is present in <see cref="Workflows"/>, keyed by its identity string —
/// the daemon never resolves anything itself.
/// </summary>
public sealed record WorkflowBundle(
    string EntryPoint,
    IReadOnlyDictionary<string, Workflow> Workflows)
{
    public Workflow? Entry => Workflows.GetValueOrDefault(EntryPoint);
}

/// <summary>
/// A pointer to a loaded workflow. Populated fields narrow the match; an omitted
/// <see cref="Version"/> means "whichever version is latest".
/// </summary>
public sealed record WorkflowReference(
    string Name,
    string? Version = null,
    string? Namespace = null,
    string? Registry = null,
    string? Digest = null)
{
    /// <summary>
    /// Renders the reference the way it was asked for, omitting the segments it does not carry.
    /// A partial reference reads as "send-email", not "send-email:" — these strings end up in
    /// "definition not found" messages, where a dangling separator looks like a daemon bug.
    /// </summary>
    public override string ToString()
    {
        var text = string.Empty;

        if (!string.IsNullOrEmpty(Registry))
            text += Registry + "/";

        if (!string.IsNullOrEmpty(Namespace))
            text += Namespace + "/";

        text += Name;

        if (!string.IsNullOrEmpty(Version))
            text += ":" + Version;

        if (!string.IsNullOrEmpty(Digest))
            text += "@" + Digest;

        return text;
    }
}
