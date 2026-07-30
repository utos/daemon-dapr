using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Common.SeedWork;
using Dapr.Workflow;
using Mediator;
using Microsoft.Extensions.Logging;
using Execution.Domain.Aggregates;
using Execution.Domain.Services;
using Execution.Features.Commands;
using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ScheduleDetachedExecutionInput(
    Guid ParentExecutionId,
    Guid ChildExecutionId,
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
        // The append path uses StreamState.Any, so a replayed activity would otherwise create a
        // second execution. The deterministic child id makes this check a reliable idempotency
        // guard: if the execution already exists, the earlier attempt succeeded.
        if (await eventStore.ExistsAsync<WorkflowExecution>(input.ChildExecutionId))
        {
            logger.LogInformation(
                "Detached execution {WorkflowExecutionId} already exists — skipping (likely replay)",
                input.ChildExecutionId);
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
        var detachedBundle = new WorkflowBundle { EntryPoint = input.SubWorkflowKey };
        detachedBundle.Workflows.Add(parentContext.Bundle.Workflows);

        var metadata = detachedBundle.Workflows[input.SubWorkflowKey].Metadata;
        var reference = new WorkflowReference
        {
            Name = metadata.Name,
            Version = metadata.Version
        };

        // A detached run inherits the parent's ambient env, matching an awaited sub-workflow.
        await mediator.Send(new ScheduleExecutionCommand(
            reference,
            detachedBundle,
            input.Input ?? new Dictionary<string, object>(),
            input.SubStartActivity,
            parentContext.Env,
            input.ChildExecutionId));

        logger.LogInformation(
            "Started detached execution {WorkflowExecutionId} for sub-workflow '{SubWorkflowKey}' " +
            "(parent {ParentWorkflowExecutionId})",
            input.ChildExecutionId, input.SubWorkflowKey, input.ParentExecutionId);

        return new object();
    }
}
