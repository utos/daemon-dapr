using Utos.Daemon.V1;
using Utos.Workflow.V1;

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
    LoadedDefinition Load(WorkflowBundle bundle);

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
