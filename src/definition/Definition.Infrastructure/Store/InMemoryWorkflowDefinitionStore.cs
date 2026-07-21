using System.Collections.Concurrent;
using Definition.Domain;
using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace Definition.Infrastructure.Store;

/// <summary>
/// In-memory definition store. Definitions are pushed by clients and are not persisted:
/// a daemon restart starts with an empty store, and clients re-load what they need.
/// Executions are unaffected either way, since each snapshots its own bundle at schedule
/// time.
/// </summary>
public sealed class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    private readonly ConcurrentDictionary<string, LoadedDefinition> _definitions = new();

    public LoadedDefinition Load(WorkflowBundle bundle)
    {
        if (!bundle.Workflows.TryGetValue(bundle.EntryPoint, out var entryWorkflow))
            throw new ArgumentException(
                $"Entry point '{bundle.EntryPoint}' not found in bundle workflows.", nameof(bundle));

        var digest = bundle.ComputeContentDigest();
        var reference = WorkflowIdentity.ReferenceFor(entryWorkflow.Metadata, digest);
        var definition = new LoadedDefinition(reference, bundle, DateTime.UtcNow);

        // Re-loading an existing identity replaces it (generally with a different digest).
        _definitions[WorkflowIdentity.Key(reference)] = definition;

        return definition;
    }

    public LoadedDefinition? Resolve(WorkflowReference reference)
    {
        var candidate = string.IsNullOrEmpty(reference.Version)
            ? LatestLoaded(reference)
            : _definitions.GetValueOrDefault(WorkflowIdentity.Key(reference));

        if (candidate is null)
            return null;

        // digest is a guard, not a selector: resolve by name(+version), then require a match.
        if (!string.IsNullOrEmpty(reference.Digest) && candidate.Reference.Digest != reference.Digest)
            return null;

        return candidate;
    }

    public IReadOnlyList<LoadedDefinition> List() => _definitions.Values.ToList();

    public int Remove(WorkflowReference reference)
    {
        var matches = MatchesToRemove(reference);
        var removed = 0;

        foreach (var match in matches)
            if (_definitions.TryRemove(WorkflowIdentity.Key(match.Reference), out _))
                removed++;

        return removed;
    }

    private IReadOnlyList<LoadedDefinition> MatchesToRemove(WorkflowReference reference)
    {
        // A content pin is unambiguous, so it overrides the omitted-version "remove ALL" rule.
        if (!string.IsNullOrEmpty(reference.Digest))
            return VersionsOf(reference)
                .Where(d => d.Reference.Digest == reference.Digest)
                .ToList();

        // An omitted version removes ALL loaded versions — removing only the latest could
        // leave an older, potentially broken version silently in place.
        if (string.IsNullOrEmpty(reference.Version))
            return VersionsOf(reference);

        var exact = _definitions.GetValueOrDefault(WorkflowIdentity.Key(reference));
        return exact is null ? [] : [exact];
    }

    private List<LoadedDefinition> VersionsOf(WorkflowReference reference)
    {
        var nameKey = WorkflowIdentity.NameKey(reference);

        return _definitions.Values
            .Where(d => WorkflowIdentity.NameKey(d.Reference) == nameKey)
            .ToList();
    }

    private LoadedDefinition? LatestLoaded(WorkflowReference reference) =>
        VersionsOf(reference)
            .OrderByDescending(d => d.Reference.Version, SemanticVersionComparer.Instance)
            .FirstOrDefault();
}
