using Common.Workflow;

namespace Execution.Application.Exceptions;

/// <summary>
/// Thrown when a schedule request references a definition that is not in the daemon's store.
/// Mapped to gRPC <c>NOT_FOUND</c> at the service boundary.
/// </summary>
public class WorkflowDefinitionNotFoundException(WorkflowReference reference)
    : Exception($"Workflow definition '{reference}' not found")
{
    public WorkflowReference Reference { get; } = reference;
}
