using Common.Infrastructure.EventHandling;
using Common.SeedWork;
using Dapr.Workflow;
using Execution.Features.Models;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Utos.Daemon.V1;
using Execution.Domain.Events;
using Execution.Domain.Services;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;
using Execution.Infrastructure.Dapr;

namespace Execution.Infrastructure.EventHandling;

public class WorkflowSchedulingProcessManager(
    IEventStoreRepository eventStore,
    DaprWorkflowClient workflowClient,
    IExecutionContextStore contextStore,
    IOptions<ExecutionLimitsOptions> limits,
    ILogger<WorkflowSchedulingProcessManager> logger) : IEventHandler
{
    public async Task HandleAsync(IDomainEvent @event, long position, CancellationToken cancellationToken = default)
    {
        switch (@event)
        {
            case WorkflowExecutionCreatedEvent e:
                await HandleWorkflowExecutionCreatedEvent(e, position, cancellationToken);
                break;
        }
    }

    public bool CanHandle(Type eventType)
    {
        return eventType == typeof(WorkflowExecutionCreatedEvent);
    }

    private async Task HandleWorkflowExecutionCreatedEvent(WorkflowExecutionCreatedEvent @event, long position,
        CancellationToken cancellationToken)
    {
        var executionId = @event.ExecutionId;
        var bundle = @event.Bundle;
        var input = @event.Input;
        var startActivity = @event.StartActivity;
        var env = @event.Env;

        try
        {
            // Step 1: Transition aggregate to Scheduled (idempotent — skip if already past Unspecified)
            var workflowExecution =
                await eventStore.LoadAsync<Domain.Aggregates.WorkflowExecution>(executionId, cancellationToken);

            if (workflowExecution.Status == ExecutionStatus.Unspecified)
            {
                workflowExecution.Schedule();
                await eventStore.SaveAsync(workflowExecution);
            }
            else
            {
                logger.LogInformation(
                    "Already in status '{Status}', skipping Schedule() — likely replay (execution {WorkflowExecutionId})",
                    workflowExecution.Status, executionId);
            }

            logger.LogInformation(
                "Execution created and scheduled (execution {WorkflowExecutionId})", executionId);

            // Step 2: Write context to store BEFORE scheduling the workflow (idempotent — skip if exists)
            if (env.Count > 0)
                logger.LogDebug(
                    "Ambient env for this run: {AmbientEnv} (execution {WorkflowExecutionId})",
                    string.Join(", ", env.Select(kvp => $"{kvp.Key}={kvp.Value}")), executionId);

            await contextStore.SaveContextAsync(new ExecutionContext
            {
                ExecutionId = executionId,
                Bundle = bundle,
                EntryPoint = bundle.EntryPoint,
                Input = input,
                Env = env
            }, cancellationToken);

            // Step 3: Schedule Dapr workflow (idempotent — handle duplicate instance)
            // Limits are captured here rather than read inside the orchestrator: a Dapr
            // workflow must replay identically, so a run keeps the limits it started with
            // even if configuration changes underneath it.
            var workflowInput = new WorkflowExecutorInput(
                executionId,
                bundle.EntryPoint,
                startActivity,
                MaxLoopedTransitions: limits.Value.MaxLoopedTransitions,
                ContinueAsNewAfterTransitions: limits.Value.ContinueAsNewAfterTransitions);

            try
            {
                await workflowClient.ScheduleNewWorkflowAsync(
                    nameof(WorkflowExecutor),
                    executionId.ToString(),
                    workflowInput);

                logger.LogInformation("Workflow scheduled starting from activity '{StartActivity}' (execution {WorkflowExecutionId})",
                    startActivity, executionId);
            }
            catch (Exception ex) when (IsDuplicateWorkflowException(ex))
            {
                logger.LogInformation(
                    "Dapr workflow already exists — likely replay, continuing (execution {WorkflowExecutionId})",
                    executionId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to schedule workflow (execution {WorkflowExecutionId})", executionId);
            throw;
        }
    }

    private static bool IsDuplicateWorkflowException(Exception ex)
    {
        if (ex is RpcException { StatusCode: StatusCode.AlreadyExists })
            return true;

        if (ex is InvalidOperationException &&
            ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
