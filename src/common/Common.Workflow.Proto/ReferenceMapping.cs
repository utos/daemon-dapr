using Domain = Common.Workflow;
using Wire = Utos.Daemon.V1;

namespace Common.Workflow.Proto;

/// <summary>
/// Maps the daemon transport's <c>WorkflowReference</c> to the domain's own. This lives in the
/// daemon package rather than the workflow one, which is exactly why the domain needed its own:
/// a reference is a first-class identity inward of the edge, not a request field.
/// </summary>
public static class ReferenceMapping
{
    public static Domain.WorkflowReference ToDomain(this Wire.WorkflowReference source) =>
        new(source.Name,
            source.HasVersion ? source.Version : null,
            source.HasNamespace ? source.Namespace : null,
            source.HasRegistry ? source.Registry : null,
            source.HasDigest ? source.Digest : null);

    public static Wire.WorkflowReference ToProto(this Domain.WorkflowReference source)
    {
        var result = new Wire.WorkflowReference { Name = source.Name };

        // proto3 `optional` distinguishes unset from empty, and callers read that difference:
        // an omitted version means "latest loaded" rather than a version literally named "".
        if (source.Version != null)
            result.Version = source.Version;

        if (source.Namespace != null)
            result.Namespace = source.Namespace;

        if (source.Registry != null)
            result.Registry = source.Registry;

        if (source.Digest != null)
            result.Digest = source.Digest;

        return result;
    }
}
