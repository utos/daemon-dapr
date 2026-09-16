using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Common.SeedWork;
using Dapr.Workflow;
using Mediator;
using Microsoft.Extensions.Logging;
using Execution.Domain.Aggregates;
using Execution.Domain.Services;
using Execution.Application.Commands;
using Common.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ScheduleDetachedExecutionInput(
    ExecutionId ParentExecutionId,
    ExecutionId ChildExecutionId,
    string SubWorkflowKey,
    string SubStartActivity,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Input);

/// <summary>
/// Starts a fire-and-forget sub-workflow as its own independent, top-level execution.
///
/// Unlike an awaited sub-workflow (a native Dapr child of the parent), a detached run goes
/// through the same scheduling pipeline as a gRPC <c>ScheduleExecution</c>: it gets its own
/// execution record, log stream, and lifecycle, appears in <c>ListExecutions</c>, and may
/// outlive the parent. The parent transitions immediately with a <c>{ execution_id }</c> handle.
///
/// This runs inside a Dapr activity (not the deterministic orchestrator), so it can dispatch a
/// command via Mediator. The child id is minted by the orchestrator (replay-safe), which lets us
/// short-circuit on replay instead of creating the execution twice.
/// </summary>
public class ScheduleDetachedExecutionActivity(
    IExecutionContextStore contextStore,
    IEventStoreRepository eventStore,
    IMediator mediator,
    ILogger<ScheduleDetachedExecutionActivity> logger)
    : WorkflowActivity<ScheduleDetachedExecutionInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, ScheduleDetachedExecutionInput input)
    {
        // A replayed activity would otherwise create the execution twice. The deterministic
        // child id makes this a cheap short-circuit; it is not the guarantee, since the stream
        // can be created between this check and the append below. The guarantee is that the
        // create expects StreamState.NoStream, so a lost race raises ConcurrencyException —
        // caught below and treated the same way.
        if (await eventStore.ExistsAsync<WorkflowExecution, ExecutionId>(input.ChildExecutionId))
        {
            LogAlreadyStarted(input.ChildExecutionId);
            return new object();
        }

        var parentContext = await contextStore.LoadContextAsync(input.ParentExecutionId);

        if (!parentContext.Bundle.Workflows.ContainsKey(input.SubWorkflowKey))
            throw new InvalidOperationException(
                $"Sub-workflow '{input.SubWorkflowKey}' not found in the bundle; " +
                "cannot start detached execution.");

        // The detached run reuses the parent's resolved workflows but points the entry point at
        // the sub-workflow, so WorkflowSchedulingProcessManager schedules and lists it as its own
        // top-level execution.
        var detachedBundle = new WorkflowBundle(input.SubWorkflowKey, parentContext.Bundle.Workflows);

        var metadata = detachedBundle.Workflows[input.SubWorkflowKey].Metadata;

        // The parent's bundle was validated when it was loaded, so its sub-workflows carry a
        // parseable version. A detached run is its own top-level execution, so it records the
        // sub-workflow's full identity rather than the parent's.
        var identity = WorkflowIdentity.From(metadata);

        try
        {
            // A detached run inherits the parent's ambient env, matching an awaited sub-workflow.
            await mediator.Send(new StartExecutionCommand(
                identity,
                null,
                detachedBundle,
                input.Input ?? new Dictionary<string, object>(),
                input.SubStartActivity,
                parentContext.Env,
                input.ChildExecutionId));
        }
        catch (ConcurrencyException)
        {
            // The stream appeared between the check above and this append, which can only be
            // an earlier attempt at this same activity: the child id is replay-safe, so no
            // other caller could have produced it.
            LogAlreadyStarted(input.ChildExecutionId);
            return new object();
        }

        logger.LogInformation(
            "Started detached execution {WorkflowExecutionId} for sub-workflow '{SubWorkflowKey}' " +
            "(parent {ParentWorkflowExecutionId})",
            input.ChildExecutionId, input.SubWorkflowKey, input.ParentExecutionId);

        return new object();
    }

    private void LogAlreadyStarted(ExecutionId childExecutionId)
    {
        logger.LogInformation(
            "Detached execution {WorkflowExecutionId} already exists — skipping (likely replay)",
            childExecutionId);
    }
}
