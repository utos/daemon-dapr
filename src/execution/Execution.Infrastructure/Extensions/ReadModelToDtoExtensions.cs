using System.Text.Json;
using Common.Workflow;
using Execution.Domain.Aggregates;
using Execution.Features.Models;
using Execution.Infrastructure.ReadModels;
using ExecutionDetails = Execution.Features.Models.ExecutionDetails;
using ExecutionSummary = Execution.Features.Models.ExecutionSummary;

namespace Execution.Infrastructure.Extensions;

/// <summary>
/// Extension methods for converting read models to POCO/DTO types
/// </summary>
public static class ReadModelToDtoExtensions
{
    // The bundle is stored as the domain model's own JSON — no proto formatter involved.

    public static ExecutionSummary ToSummaryDto(this WorkflowExecutionReadModel readModel)
    {
        return new ExecutionSummary
        {
            Id = readModel.Id,
            Status = ParseStatus(readModel.Status),
            Workflow = readModel.Identity,
            WorkflowDigest = readModel.WorkflowDigest,
            CreatedAt = readModel.CreatedAt,
            ScheduledAt = readModel.ScheduledAt,
            StartedAt = readModel.StartedAt,
            FinishedAt = readModel.CompletedAt ?? readModel.FailedAt,
            ErrorMessage = readModel.ErrorMessage != null && readModel.ErrorMessage.Length > 200
                ? readModel.ErrorMessage.Substring(0, 200)
                : readModel.ErrorMessage
        };
    }

    public static ExecutionDetails ToDetailsDto(this WorkflowExecutionReadModel readModel)
    {
        WorkflowBundle? bundle = null;
        if (!string.IsNullOrEmpty(readModel.WorkflowDefinitionJson))
            try
            {
                bundle = JsonSerializer.Deserialize<WorkflowBundle>(readModel.WorkflowDefinitionJson);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialize workflow bundle for execution {readModel.Id}", ex);
            }

        Dictionary<string, object> input = new();
        if (!string.IsNullOrEmpty(readModel.InputJson))
            try
            {
                input = JsonSerializer.Deserialize<Dictionary<string, object>>(readModel.InputJson) ??
                        new Dictionary<string, object>();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to deserialize payload for execution {readModel.Id}", ex);
            }

        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(readModel.EnvJson))
            try
            {
                env = JsonSerializer.Deserialize<Dictionary<string, string>>(readModel.EnvJson) ??
                      new Dictionary<string, string>();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Failed to deserialize env for execution {readModel.Id}", ex);
            }

        return new ExecutionDetails
        {
            Id = readModel.Id,
            Status = ParseStatus(readModel.Status),
            Workflow = readModel.Identity,
            WorkflowDigest = readModel.WorkflowDigest,
            Bundle = bundle ??
                     throw new InvalidOperationException($"Workflow bundle is missing for execution {readModel.Id}"),
            Input = input,
            Env = env,
            CreatedAt = readModel.CreatedAt,
            ScheduledAt = readModel.ScheduledAt,
            StartedAt = readModel.StartedAt,
            CompletedAt = readModel.CompletedAt,
            FailedAt = readModel.FailedAt,
            ErrorMessage = readModel.ErrorMessage
        };
    }

    private static ExecutionStatus ParseStatus(string status)
    {
        return status switch
        {
            ReadModelStatus.Unspecified => ExecutionStatus.Unspecified,
            ReadModelStatus.Scheduled => ExecutionStatus.Scheduled,
            ReadModelStatus.Active => ExecutionStatus.Active,
            ReadModelStatus.Completed => ExecutionStatus.Completed,
            ReadModelStatus.Failed => ExecutionStatus.Failed,
            _ => throw new ArgumentException($"Unknown status: {status}")
        };
    }
}
