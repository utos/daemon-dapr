using Common.Workflow;

namespace Definition.Domain;

/// <summary>
/// The daemon's local store of workflow definitions. Definitions are always pushed in
/// (never fetched by the daemon) and keyed by their canonical
/// "[registry/][namespace/]name:version" identity.
/// </summary>
public interface IWorkflowDefinitionStore
{
    /// <summary>
    /// Stores a fully-resolved bundle under the identity derived from its entry-point
    /// workflow metadata. Re-loading an existing identity replaces it.
    /// </summary>
    /// <param name="digest">
    /// Content digest of the bundle. Passed in rather than computed here: it is defined over the
    /// canonical wire serialization, so it is the transport edge — where the bundle still exists
    /// in that form — that can compute a value matching what the CLI and registry compute.
    /// </param>
    LoadedDefinition Load(WorkflowBundle bundle, string digest);

    /// <summary>
    /// Resolves a reference to a loaded definition. An omitted version means "latest
    /// loaded" — the highest semver among loaded versions of that identity.
    /// Returns null when nothing matches.
    /// </summary>
    LoadedDefinition? Resolve(WorkflowReference reference);

    /// <summary>All loaded definitions, one per identity.</summary>
    IReadOnlyList<LoadedDefinition> List();

    /// <summary>
    /// Removes loaded definitions and returns how many were removed. An omitted version
    /// removes ALL versions of that "[registry/][namespace/]name"; a set digest instead
    /// narrows removal to the single version whose content matches, even when the version
    /// is omitted.
    /// </summary>
    int Remove(WorkflowReference reference);
}
