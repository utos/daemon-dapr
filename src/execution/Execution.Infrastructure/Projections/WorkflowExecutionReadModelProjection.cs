using System.Text.Json;
using Common.Infrastructure.Projections;
using Common.SeedWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Execution.Domain.Events;
using Execution.Infrastructure.Data;
using Execution.Infrastructure.ReadModels;

namespace Execution.Infrastructure.Projections;

public class WorkflowExecutionReadModelProjection(
    WorkflowExecutionDbContext dbContext,
    ILogger<WorkflowExecutionReadModelProjection> logger)
    : DbProjection(dbContext, logger)
{
    // Persisted as the domain model's own JSON, matching what ReadModelToDtoExtensions reads back.

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };

    public override bool CanHandle(Type eventType)
    {
        return eventType == typeof(WorkflowExecutionCreatedEvent)
               || eventType == typeof(SubWorkflowCreatedEvent)
               || eventType == typeof(WorkflowExecutionScheduledEvent)
               || eventType == typeof(WorkflowExecutionActiveEvent)
               || eventType == typeof(WorkflowExecutionCompletedEvent)
               || eventType == typeof(WorkflowExecutionFailedEvent);
    }

    protected override async Task ProjectEventAsync(IDomainEvent @event, long position,
        CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case WorkflowExecutionCreatedEvent e:
                await HandleCreatedEvent(e, cancellationToken);
                break;

            case SubWorkflowCreatedEvent e:
                await HandleSubWorkflowCreatedEvent(e, cancellationToken);
                break;

            case WorkflowExecutionScheduledEvent e:
                await HandleScheduledEvent(e, cancellationToken);
                break;

            case WorkflowExecutionActiveEvent e:
                await HandleActiveEvent(e, cancellationToken);
                break;

            case WorkflowExecutionCompletedEvent e:
                await HandleCompletedEvent(e, cancellationToken);
                break;

            case WorkflowExecutionFailedEvent e:
                await HandleFailedEvent(e, cancellationToken);
                break;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private Task HandleCreatedEvent(WorkflowExecutionCreatedEvent @event, CancellationToken _)
    {
        // Resolve the entry workflow from the bundle to get name/version
        var entryWorkflow = @event.Bundle.Workflows[@event.Bundle.EntryPoint];

        var readModel = new WorkflowExecutionReadModel
        {
            Id = @event.ExecutionId,
            ParentExecutionId = null,
            Status = ReadModelStatus.Unspecified,
            WorkflowName = entryWorkflow.Metadata.Name,
            WorkflowVersion = entryWorkflow.Metadata.Version,
            // Store empty proto strings as null so "no namespace/registry" is a single
            // representation for both querying and response mapping.
            WorkflowNamespace = NullIfEmpty(entryWorkflow.Metadata.Namespace),
            WorkflowRegistry = NullIfEmpty(entryWorkflow.Metadata.Registry),
            WorkflowDigest = NullIfEmpty(@event.Digest),
            WorkflowDefinitionJson = JsonSerializer.Serialize(@event.Bundle),
            InputJson = @event.Input.Count > 0 ? JsonSerializer.Serialize(@event.Input, JsonOptions) : null,
            EnvJson = @event.Env.Count > 0 ? JsonSerializer.Serialize(@event.Env) : null
        };

        dbContext.WorkflowExecutions.Add(readModel);

        logger.LogDebug("Created read model for execution {ExecutionId}", @event.ExecutionId);

        return Task.CompletedTask;
    }

    private Task HandleSubWorkflowCreatedEvent(SubWorkflowCreatedEvent @event, CancellationToken _)
    {
        var readModel = new WorkflowExecutionReadModel
        {
            Id = @event.ExecutionId,
            ParentExecutionId = @event.ParentExecutionId,
            Status = ReadModelStatus.Active,
            WorkflowName = @event.WorkflowName,
            WorkflowVersion = @event.WorkflowVersion,
            StartedAt = @event.DateOccurred
        };

        dbContext.WorkflowExecutions.Add(readModel);

        logger.LogDebug("Created read model for sub-workflow execution {ExecutionId} (parent: {ParentId})",
            @event.ExecutionId, @event.ParentExecutionId);

        return Task.CompletedTask;
    }

    private async Task HandleScheduledEvent(WorkflowExecutionScheduledEvent @event, CancellationToken cancellationToken)
    {
        var readModel = await dbContext.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.Id == @event.ExecutionId, cancellationToken);

        if (readModel == null)
        {
            logger.LogWarning("Read model not found for execution {ExecutionId}", @event.ExecutionId);
            return;
        }

        readModel.Status = ReadModelStatus.Scheduled;
        readModel.ScheduledAt = @event.DateOccurred;

        logger.LogDebug("Updated read model to {Status} for execution {ExecutionId}", ReadModelStatus.Scheduled,
            @event.ExecutionId);
    }

    private async Task HandleActiveEvent(WorkflowExecutionActiveEvent @event, CancellationToken cancellationToken)
    {
        var readModel = await dbContext.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.Id == @event.ExecutionId, cancellationToken);

        if (readModel == null)
        {
            logger.LogWarning("Read model not found for execution {ExecutionId}", @event.ExecutionId);
            return;
        }

        readModel.Status = ReadModelStatus.Active;
        readModel.StartedAt = @event.DateOccurred;

        logger.LogDebug("Updated read model to {Status} for execution {ExecutionId}", ReadModelStatus.Active,
            @event.ExecutionId);
    }

    private async Task HandleCompletedEvent(WorkflowExecutionCompletedEvent @event, CancellationToken cancellationToken)
    {
        var readModel = await dbContext.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.Id == @event.ExecutionId, cancellationToken);

        if (readModel == null)
        {
            logger.LogWarning("Read model not found for execution {ExecutionId}", @event.ExecutionId);
            return;
        }

        readModel.Status = ReadModelStatus.Completed;
        readModel.CompletedAt = @event.DateOccurred;

        logger.LogDebug("Updated read model to {Status} for execution {ExecutionId}", ReadModelStatus.Completed,
            @event.ExecutionId);
    }

    private async Task HandleFailedEvent(WorkflowExecutionFailedEvent @event, CancellationToken cancellationToken)
    {
        var readModel = await dbContext.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.Id == @event.ExecutionId, cancellationToken);

        if (readModel == null)
        {
            logger.LogWarning("Read model not found for execution {ExecutionId}", @event.ExecutionId);
            return;
        }

        readModel.Status = ReadModelStatus.Failed;
        readModel.FailedAt = @event.DateOccurred;
        readModel.ErrorMessage = @event.Reason;

        logger.LogDebug("Updated read model to {Status} for execution {ExecutionId}", ReadModelStatus.Failed,
            @event.ExecutionId);
    }
}
