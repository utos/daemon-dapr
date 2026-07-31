using Common.Workflow;

namespace Definition.Domain;

/// <summary>
/// A workflow definition held in the daemon's local store, as pushed by
/// <c>DefinitionService.LoadWorkflow</c>.
/// </summary>
/// <param name="Reference">Resolved identity — version and digest always populated.</param>
/// <param name="Bundle">The fully-resolved bundle exactly as loaded.</param>
/// <param name="LoadedAt">When the definition entered the store.</param>
public sealed record LoadedDefinition(
    WorkflowReference Reference,
    WorkflowBundle Bundle,
    DateTime LoadedAt)
{
    /// <summary>Human-readable description from the entry-point workflow's metadata.</summary>
    public string? Description =>
        string.IsNullOrEmpty(Bundle.Entry?.Metadata.Description)
            ? null
            : Bundle.Entry.Metadata.Description;
}
