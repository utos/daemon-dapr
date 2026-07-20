namespace Common.Constants;

/// <summary>
/// Document-level identifiers a <c>Workflow</c> declares.
/// Since Utos spec v0.0.10 these are free-form strings rather than enums:
/// <c>api_version</c> is "group/version" and <c>kind</c> is a bare PascalCase noun.
/// </summary>
public static class WorkflowDocument
{
    /// <summary>API group and spec generation, currently "utos.io/v1".</summary>
    public const string ApiVersion = "utos.io/v1";

    /// <summary>Resource kind within the api_version group, currently "Workflow".</summary>
    public const string Kind = "Workflow";
}
