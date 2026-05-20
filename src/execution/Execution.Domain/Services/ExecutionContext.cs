using Utos.Workflow.V1;

namespace Execution.Domain.Services;

/// <summary>
/// Immutable execution context entity. Write-once at schedule, read-many by activities.
/// Stored in EF Core to keep protobuf/gRPC types out of Dapr persistence.
/// </summary>
public sealed class ExecutionContext
{
    public Guid ExecutionId { get; init; }
    public WorkflowBundle Bundle { get; init; } = null!;
    public string EntryPoint { get; init; } = null!;
    public Dictionary<string, object> Input { get; init; } = new();
}
