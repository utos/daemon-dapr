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
/// <param name="Output">
/// A JSON Schema 2020-12 document describing what this workflow returns, or null where it
/// declares nothing. A result that violates it fails the run, and that is not a policy an author
/// opts into: a workflow returning the wrong shape is broken, and the caller that trusted its
/// contract is the one that would otherwise carry the damage.
/// </param>
/// <param name="Emits">
/// A JSON Schema 2020-12 document describing <em>one</em> value this workflow appends to its
/// output stream — not the stream, which is a sequence of values each of which must satisfy it.
/// A contract in its own right: a consumer's <c>onEmitted</c> rules depend on it exactly as a
/// caller depends on a result.
/// </param>
/// <param name="Env">
/// A JSON Schema 2020-12 document describing the ambient environment this workflow requires.
/// Every property is a string, because a run's env is string-to-string. Deliberately <em>not</em>
/// a closed object: env is shared across a run tree and a sub-workflow inherits its parent's, so
/// a document names what it requires and ignores the rest.
/// </param>
/// <remarks>
/// The three schema slots are optional and null means "declares nothing", which the empty schema
/// satisfies. They are nullable for the same reason <c>AmbientEnv</c> and <c>Digest</c> are on the
/// created event: a bundle persisted before schemas existed reads back unchanged, so no upcaster
/// and no <c>[EventType(Version = n)]</c> bump is needed.
/// </remarks>
public sealed record WorkflowSpec(
    IReadOnlyDictionary<string, WorkflowActivity> Activities,
    IReadOnlyDictionary<string, string>? Dependencies = null,
    ValueMap? Output = null,
    ValueMap? Emits = null,
    ValueMap? Env = null)
{
    public static WorkflowSpec Empty { get; } = new(new Dictionary<string, WorkflowActivity>());

    public IReadOnlyDictionary<string, string> DependencyMap =>
        Dependencies ?? new Dictionary<string, string>();
}

/// <summary>
/// A fully-resolved set of workflows plus the one execution starts from. Every dependency a
/// workflow references is present in <see cref="Workflows"/>, keyed by its identity string —
/// the daemon never resolves anything itself.
/// <para>
/// Persisted as JSON in three places, which change together: the event
/// (<c>WorkflowExecutionCreatedEvent.Bundle</c>), the execution context (<c>BundleJson</c>) and
/// the read model (<c>WorkflowDefinitionJson</c>). A change to this type's shape, or to any type
/// it contains, has to stay readable in all three for rows written before it.
/// </para>
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
