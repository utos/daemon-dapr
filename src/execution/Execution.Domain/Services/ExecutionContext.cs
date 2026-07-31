using Common.Workflow;

namespace Execution.Domain.Services;

/// <summary>
/// Immutable execution context entity. Write-once at schedule, read-many by activities.
/// Stored in EF Core rather than passed through Dapr, so a bundle of any size crosses an
/// activity boundary as an id.
/// </summary>
public sealed class ExecutionContext
{
    public Guid ExecutionId { get; init; }
    public WorkflowBundle Bundle { get; init; } = null!;
    public string EntryPoint { get; init; } = null!;
    public Dictionary<string, object> Input { get; init; } = new();

    /// <summary>
    /// Ambient environment for the run, available to every activity as {{ env.x }}.
    /// Sub-workflow executions inherit their parent's.
    /// </summary>
    public Dictionary<string, string> Env { get; init; } = new();
}
