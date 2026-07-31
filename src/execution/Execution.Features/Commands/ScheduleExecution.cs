using Common.Workflow;
using Execution.Domain.Aggregates;
using Mediator;

namespace Execution.Features.Commands;

/// <summary>
/// Schedules a run of a definition already loaded into the daemon's store.
/// <paramref name="Workflow"/> is the resolved identity of that definition and
/// <paramref name="Bundle"/> the content it resolved to — snapshotted into the execution
/// so a later re-load or unload cannot affect a run in flight.
/// </summary>
public record ScheduleExecutionCommand(
    WorkflowReference Workflow,
    WorkflowBundle Bundle,
    Dictionary<string, object> Input,
    string StartActivity,
    // Ambient config available to every activity via {{ env.x }}, unlike Input which
    // reaches only the start activity. Non-secret only.
    Dictionary<string, string> Env,
    // Caller-supplied execution id. Set by the detached sub-workflow path, which mints a
    // replay-safe id in the orchestrator so scheduling is idempotent across Dapr replays.
    // Null for the gRPC path, which lets the aggregate mint its own.
    Guid? ExecutionId = null) : ICommand<ScheduleExecutionResult>;

public record ScheduleExecutionResult(Guid Id, ExecutionStatus Status);
