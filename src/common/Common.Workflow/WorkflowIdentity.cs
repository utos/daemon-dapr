using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;

namespace Common.Workflow;

/// <summary>
/// The canonical <c>[registry/][namespace/]name:version</c> identity of a workflow — fully
/// resolved, with the version always present. This is what a definition is stored under and what
/// an execution records it ran.
///
/// Distinct from <see cref="WorkflowReference"/> on purpose: a reference is a possibly-partial
/// <em>request</em> (an omitted version means "latest", a digest is an optional guard), whereas an
/// identity is the <em>answer</em>. Keeping them apart is what stops a half-populated reference
/// from being used where a resolved one is required.
///
/// Optional segments are normalised to null on construction, so an empty registry and an absent
/// one cannot produce two identities that format the same but compare differently.
/// </summary>
[JsonConverter(typeof(WorkflowIdentityJsonConverter))]
public sealed record WorkflowIdentity
{
    private WorkflowIdentity(string name, SemanticVersion version, string? @namespace, string? registry)
    {
        Name = name;
        Version = version;
        Namespace = @namespace;
        Registry = registry;
    }

    public string Name { get; }

    public SemanticVersion Version { get; }

    public string? Namespace { get; }

    public string? Registry { get; }

    public static WorkflowIdentity Create(string name, SemanticVersion version,
        string? @namespace = null, string? registry = null)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A workflow name is required.", nameof(name));

        // A segment carrying a separator would format into an identity that parses back as
        // something else, so the round-trip guarantee has to be enforced here.
        RejectSeparators(name, nameof(name));
        RejectSeparators(@namespace, nameof(@namespace));

        if (registry is not null && registry.Contains('/'))
            throw new ArgumentException($"A registry cannot contain '/': '{registry}'.", nameof(registry));

        return new WorkflowIdentity(name, version, NullIfEmpty(@namespace), NullIfEmpty(registry));
    }

    /// <summary>
    /// Builds the identity of an authored workflow. The version must already be known valid —
    /// callers on the load path validate the metadata first, so a failure here is a bug rather
    /// than bad input.
    /// </summary>
    public static WorkflowIdentity From(WorkflowMetadata metadata) =>
        TryFrom(metadata, out var identity)
            ? identity
            : throw new FormatException(
                $"Workflow '{metadata.Name}' has an invalid version '{metadata.Version}'.");

    public static bool TryFrom(WorkflowMetadata metadata, [NotNullWhen(true)] out WorkflowIdentity? identity)
    {
        identity = null;

        if (!SemanticVersion.TryParse(metadata.Version, out var version))
            return false;

        if (string.IsNullOrWhiteSpace(metadata.Name))
            return false;

        identity = new WorkflowIdentity(
            metadata.Name,
            version,
            NullIfEmpty(metadata.Namespace),
            NullIfEmpty(metadata.Registry));

        return true;
    }

    public static WorkflowIdentity Parse(string identity) =>
        TryParse(identity, out var parsed)
            ? parsed
            : throw new FormatException($"'{identity}' is not a valid workflow identity.");

    public static bool TryParse([NotNullWhen(true)] string? identity,
        [NotNullWhen(true)] out WorkflowIdentity? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(identity))
            return false;

        // Last ':' rather than first, so a registry carrying a port still splits correctly.
        var separator = identity.LastIndexOf(':');
        if (separator < 0)
            return false;

        var path = identity[..separator];
        var versionText = identity[(separator + 1)..];

        // A '/' after the separator means that ':' belonged to a registry port, not the version.
        if (versionText.Contains('/'))
            return false;

        if (!SemanticVersion.TryParse(versionText, out var version))
            return false;

        var segments = path.Split('/');
        if (segments.Length > 3 || segments.Any(string.IsNullOrEmpty))
            return false;

        var name = segments[^1];
        var @namespace = segments.Length >= 2 ? segments[^2] : null;
        var registry = segments.Length == 3 ? segments[0] : null;

        parsed = new WorkflowIdentity(name, version, @namespace, registry);

        return true;
    }

    /// <summary>
    /// The identity without its version, e.g. "acme/send-email". Groups every loaded version of
    /// the same workflow.
    /// </summary>
    public string NameKey => Prefix() + Name;

    public override string ToString() => $"{Prefix()}{Name}:{Version}";

    private string Prefix()
    {
        var prefix = string.Empty;

        if (Registry is not null)
            prefix += Registry + "/";

        if (Namespace is not null)
            prefix += Namespace + "/";

        return prefix;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static void RejectSeparators(string? segment, string parameterName)
    {
        if (segment is not null && (segment.Contains('/') || segment.Contains(':')))
            throw new ArgumentException(
                $"A workflow {parameterName} cannot contain '/' or ':': '{segment}'.", parameterName);
    }
}
