using Common.Workflow;

namespace Definition.Application.Exceptions;

/// <summary>
/// Thrown when a reference does not resolve to anything in the daemon's store.
/// Mapped to gRPC <c>NOT_FOUND</c> at the service boundary.
/// </summary>
public class WorkflowDefinitionNotFoundException(WorkflowReference reference)
    : Exception($"Workflow definition '{reference}' not found")
{
    public WorkflowReference Reference { get; } = reference;
}
