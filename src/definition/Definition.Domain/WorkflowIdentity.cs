using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace Definition.Domain;

/// <summary>
/// The canonical "[registry/][namespace/]name:version" identity of a workflow.
/// This is the same key format used by <see cref="WorkflowBundle.Workflows"/>.
/// </summary>
public static class WorkflowIdentity
{
    /// <summary>
    /// Builds the full identity key, e.g. "registry.example.com/acme/send-email:1.0.0".
    /// </summary>
    public static string Key(string? registry, string? @namespace, string name, string version)
    {
        var prefix = string.Empty;

        if (!string.IsNullOrEmpty(registry))
            prefix += registry + "/";

        if (!string.IsNullOrEmpty(@namespace))
            prefix += @namespace + "/";

        return $"{prefix}{name}:{version}";
    }

    public static string Key(WorkflowMetadata metadata) =>
        Key(metadata.Registry, metadata.Namespace, metadata.Name, metadata.Version);

    public static string Key(WorkflowReference reference) =>
        Key(reference.Registry, reference.Namespace, reference.Name, reference.Version);

    /// <summary>
    /// The identity key without the version, e.g. "acme/send-email". Used to group all
    /// loaded versions of the same workflow.
    /// </summary>
    public static string NameKey(string? registry, string? @namespace, string name)
    {
        var prefix = string.Empty;

        if (!string.IsNullOrEmpty(registry))
            prefix += registry + "/";

        if (!string.IsNullOrEmpty(@namespace))
            prefix += @namespace + "/";

        return prefix + name;
    }

    public static string NameKey(WorkflowMetadata metadata) =>
        NameKey(metadata.Registry, metadata.Namespace, metadata.Name);

    public static string NameKey(WorkflowReference reference) =>
        NameKey(reference.Registry, reference.Namespace, reference.Name);

    /// <summary>
    /// Builds a resolved reference (version and digest always populated) from a bundle's
    /// entry-point metadata.
    /// </summary>
    public static WorkflowReference ReferenceFor(WorkflowMetadata metadata, string digest)
    {
        var reference = new WorkflowReference
        {
            Name = metadata.Name,
            Version = metadata.Version,
            Digest = digest
        };

        if (!string.IsNullOrEmpty(metadata.Registry))
            reference.Registry = metadata.Registry;

        if (!string.IsNullOrEmpty(metadata.Namespace))
            reference.Namespace = metadata.Namespace;

        return reference;
    }
}
