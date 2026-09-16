using System.Collections.Concurrent;
using Common.Workflow;
using Definition.Domain;

namespace Definition.Infrastructure.Store;

/// <summary>
/// In-memory definition store. Definitions are pushed by clients and are not persisted:
/// a daemon restart starts with an empty store, and clients re-load what they need.
/// Executions are unaffected either way, since each snapshots its own bundle at schedule
/// time.
/// </summary>
public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly ConcurrentDictionary<WorkflowIdentity, LoadedDefinition> _definitions = new();

    public LoadedDefinition Load(WorkflowBundle bundle, string digest)
    {
        if (!bundle.Workflows.TryGetValue(bundle.EntryPoint, out var entryWorkflow))
            throw new ArgumentException(
                $"Entry point '{bundle.EntryPoint}' not found in bundle workflows.", nameof(bundle));

        // Safe to throw on a bad version: the transport validates the bundle before loading it.
        var identity = WorkflowIdentity.From(entryWorkflow.Metadata);
        var definition = new LoadedDefinition(identity, digest, bundle, DateTime.UtcNow);

        // Re-loading an existing identity replaces it (generally with a different digest).
        _definitions[identity] = definition;

        return definition;
    }

    public LoadedDefinition? Resolve(WorkflowReference reference)
    {
        var candidate = string.IsNullOrEmpty(reference.Version)
            ? LatestLoaded(reference)
            : ExactMatch(reference);

        if (candidate is null)
            return null;

        // digest is a guard, not a selector: resolve by name(+version), then require a match.
        if (!string.IsNullOrEmpty(reference.Digest) && candidate.Digest != reference.Digest)
            return null;

        return candidate;
    }

    public IReadOnlyList<LoadedDefinition> List() => _definitions.Values.ToList();

    public int Remove(WorkflowReference reference)
    {
        var matches = MatchesToRemove(reference);
        var removed = 0;

        foreach (var match in matches)
            if (_definitions.TryRemove(match.Identity, out _))
                removed++;

        return removed;
    }

    private IReadOnlyList<LoadedDefinition> MatchesToRemove(WorkflowReference reference)
    {
        // A content pin is unambiguous, so it overrides the omitted-version "remove ALL" rule.
        if (!string.IsNullOrEmpty(reference.Digest))
            return VersionsOf(reference)
                .Where(d => d.Digest == reference.Digest)
                .ToList();

        // An omitted version removes ALL loaded versions — removing only the latest could
        // leave an older, potentially broken version silently in place.
        if (string.IsNullOrEmpty(reference.Version))
            return VersionsOf(reference);

        var exact = ExactMatch(reference);
        return exact is null ? [] : [exact];
    }

    /// <summary>
    /// Looks up the one definition a fully-specified reference names. A version the daemon
    /// cannot parse matches nothing, rather than throwing: it is a client-supplied string.
    /// </summary>
    private LoadedDefinition? ExactMatch(WorkflowReference reference)
    {
        if (!SemanticVersion.TryParse(reference.Version, out var version))
            return null;

        var identity = WorkflowIdentity.Create(
            reference.Name, version, reference.Namespace, reference.Registry);

        return _definitions.GetValueOrDefault(identity);
    }

    private List<LoadedDefinition> VersionsOf(WorkflowReference reference) =>
        _definitions.Values
            .Where(d => NamesTheSameWorkflow(d.Identity, reference))
            .ToList();

    private LoadedDefinition? LatestLoaded(WorkflowReference reference) =>
        VersionsOf(reference).MaxBy(d => d.Identity.Version);

    /// <summary>
    /// Whether a loaded identity is a version of the workflow the reference names. Compared
    /// segment by segment, treating an empty optional segment as an absent one — the same
    /// normalisation <see cref="WorkflowIdentity"/> applies to itself.
    /// </summary>
    private static bool NamesTheSameWorkflow(WorkflowIdentity identity, WorkflowReference reference) =>
        identity.Name == reference.Name
        && identity.Namespace == NullIfEmpty(reference.Namespace)
        && identity.Registry == NullIfEmpty(reference.Registry);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
