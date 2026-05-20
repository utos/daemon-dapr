using Common.Infrastructure.EventHandling;
using Common.SeedWork;
using Dapr.Workflow;
using Grpc.Core;
using Microsoft.Extensions.Logging;
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
    IWorkflowLogger workflowLogger,
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
                    "[{ExecutionId}] Already in status '{Status}', skipping Schedule() (likely replay)",
                    executionId, workflowExecution.Status);
            }

            // Log lifecycle event to observability stream
            await workflowLogger.LogAsync(executionId, "system", "lifecycle",
                WorkflowLogLevel.Info, "Execution created and scheduled");

            // Step 2: Write context to store BEFORE scheduling the workflow (idempotent — skip if exists)
            await contextStore.SaveContextAsync(new ExecutionContext
            {
                ExecutionId = executionId,
                Bundle = bundle,
                EntryPoint = bundle.EntryPoint,
                Input = input
            }, cancellationToken);

            // Step 3: Schedule Dapr workflow (idempotent — handle duplicate instance)
            var workflowInput = new WorkflowExecutorInput(
                executionId,
                bundle.EntryPoint,
                startActivity);

            try
            {
                await workflowClient.ScheduleNewWorkflowAsync(
                    nameof(WorkflowExecutor),
                    executionId.ToString(),
                    workflowInput);

                logger.LogInformation("[{ExecutionId}] Workflow scheduled starting from activity '{StartActivity}'",
                    executionId, startActivity);
            }
            catch (Exception ex) when (IsDuplicateWorkflowException(ex))
            {
                logger.LogInformation(
                    "[{ExecutionId}] Dapr workflow already exists (likely replay), continuing",
                    executionId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{ExecutionId}] Failed to schedule workflow", executionId);
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
