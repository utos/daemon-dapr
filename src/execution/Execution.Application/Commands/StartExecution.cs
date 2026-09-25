using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Aggregates;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Starts a run from content that has already been resolved. <paramref name="Workflow"/> is the
/// resolved identity, <paramref name="Digest"/> the content it is pinned to, and
/// <paramref name="Bundle"/> the content itself — snapshotted into the execution so a later
/// re-load or unload cannot affect a run in flight.
///
/// Two callers reach this: <see cref="ScheduleExecutionCommand"/>, which resolves a reference
/// against the definition store first, and the detached sub-workflow path, which already holds
/// the parent's snapshotted bundle and never consults the store.
/// </summary>
public record StartExecutionCommand(
    WorkflowIdentity Workflow,
    string? Digest,
    WorkflowBundle Bundle,
    Dictionary<string, object> Input,
    string StartActivity,
    // Ambient config available to every activity via {{ env.x }}, unlike Input which
    // reaches only the start activity. Non-secret only.
    Dictionary<string, string> Env,
    // Caller-supplied execution id. Set by the detached sub-workflow path, which mints a
    // replay-safe id in the orchestrator so scheduling is idempotent across Dapr replays.
    // Null for the gRPC path, which lets the aggregate mint its own.
    ExecutionId? ExecutionId = null,
    // The run tree a `workflow.spawn` is starting this run from, and the whole of what distinguishes
    // the two ways a blob enters a run from outside. Set, the input's blobs must already belong to
    // that tree and need not be durable; null, this is a client's schedule and each blob must be
    // durable instead. Either way the accepted ones are attached to the tree this run roots.
    ExecutionId? SpawningTree = null) : ICommand<ScheduleExecutionResult>;

public record ScheduleExecutionResult(ExecutionId Id, ExecutionStatus Status);
