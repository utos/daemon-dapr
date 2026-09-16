using Common.SeedWork;
using Common.Workflow;

namespace Execution.Domain.Services;

/// <summary>
/// Immutable execution context entity. Write-once at schedule, read-many by activities.
/// Stored in EF Core rather than passed through Dapr, so a bundle of any size crosses an
/// activity boundary as an id.
/// </summary>
public sealed class ExecutionContext
{
    public ExecutionId ExecutionId { get; init; }

    /// <summary>
    /// The top-level run this context belongs to; null on that run's own context.
    /// <para>
    /// The only record anywhere that a sub-workflow execution existed. A <c>workflow.call</c>
    /// child is a child orchestration rather than an execution of its own — no aggregate stream,
    /// no read-model row — so without this there is nothing to enumerate its id from, and the
    /// output stream it wrote could never be found again, let alone reclaimed.
    /// </para>
    /// <para>
    /// The <em>root</em> rather than the immediate parent, so one query reaches a whole tree: a
    /// child that itself calls a sub-workflow would otherwise hide its grandchild one level
    /// further down.
    /// </para>
    /// </summary>
    public ExecutionId? RootExecutionId { get; init; }
    public WorkflowBundle Bundle { get; init; } = null!;
    public string EntryPoint { get; init; } = null!;
    public Dictionary<string, object> Input { get; init; } = new();

    /// <summary>
    /// Ambient environment for the run, available to every activity as {{ env.x }}.
    /// Sub-workflow executions inherit their parent's.
    /// </summary>
    public Dictionary<string, string> Env { get; init; } = new();
}
