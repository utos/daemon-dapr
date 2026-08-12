using Google.Protobuf.WellKnownTypes;
using Common.Constants;
using Common.Workflow.Proto;
using Execution.Application.Models;
using DomainExecutionStatus = Execution.Domain.Aggregates.ExecutionStatus;
using GrpcExecution = Utos.Daemon.V1.ScheduleExecutionResponse;
using GrpcExecutionSummary = Utos.Daemon.V1.ExecutionSummary;
using GrpcExecutionDetails = Utos.Daemon.V1.GetExecutionResponse;
using GrpcExecutionStatus = Utos.Daemon.V1.ExecutionStatus;
using GrpcWorkflow = Utos.Workflows.V1.Workflow;
using GrpcWorkflowSpec = Utos.Workflows.V1.WorkflowSpec;
using GrpcWorkflowActivity = Utos.Workflows.V1.WorkflowActivity;
using GrpcHttpActivityConfig = Utos.Workflows.V1.HttpActivityConfig;
using GrpcWorkflowMetadata = Utos.Workflows.V1.WorkflowMetadata;
using GrpcExecutionPayload = Utos.Daemon.V1.ExecutionPayload;
using GrpcTransitionTarget = Utos.Workflows.V1.TransitionTarget;
using GrpcWorkflowReference = Utos.Daemon.V1.WorkflowReference;
using GrpcWorkflowError = Utos.Workflows.V1.WorkflowError;

namespace Execution.Api.Extensions;

/// <summary>
/// Extension methods for converting POCO/DTO types to gRPC-generated types
/// </summary>
public static class DtoToGrpcExtensions
{
    /// <summary>
    /// Maps the daemon's own lifecycle state onto the wire enum. The two are deliberately
    /// separate types: the domain one is durable aggregate state and must not shift because a
    /// transport contract was renumbered.
    /// </summary>
    public static GrpcExecutionStatus ToGrpc(this DomainExecutionStatus status)
    {
        return status switch
        {
            DomainExecutionStatus.Scheduled => GrpcExecutionStatus.Scheduled,
            DomainExecutionStatus.Active => GrpcExecutionStatus.Active,
            DomainExecutionStatus.Completed => GrpcExecutionStatus.Completed,
            DomainExecutionStatus.Failed => GrpcExecutionStatus.Failed,
            DomainExecutionStatus.Cancelled => GrpcExecutionStatus.Cancelled,
            _ => GrpcExecutionStatus.Unspecified
        };
    }

    public static GrpcExecutionSummary ToGrpc(this ExecutionSummary dto)
    {
        var grpc = new GrpcExecutionSummary
        {
            Id = dto.Id.ToString(),
            Status = dto.Status.ToGrpc(),
            Workflow = dto.Workflow.ToProto(dto.WorkflowDigest),
            CreatedAt = Timestamp.FromDateTime(dto.CreatedAt.ToUniversalTime())
        };

        if (dto.ScheduledAt.HasValue)
            grpc.ScheduledAt = Timestamp.FromDateTime(dto.ScheduledAt.Value.ToUniversalTime());

        if (dto.StartedAt.HasValue)
            grpc.StartedAt = Timestamp.FromDateTime(dto.StartedAt.Value.ToUniversalTime());

        if (dto.FinishedAt.HasValue)
            grpc.FinishedAt = Timestamp.FromDateTime(dto.FinishedAt.Value.ToUniversalTime());

        // Never folded into Error: a run stopped on purpose has not failed.
        if (!string.IsNullOrEmpty(dto.CancellationReason))
            grpc.CancellationReason = dto.CancellationReason;

        if (!string.IsNullOrEmpty(dto.ErrorMessage))
            grpc.Error = new GrpcWorkflowError
            {
                Code = WorkflowErrorCode.ExecutionFailed,
                Message = dto.ErrorMessage
            };

        return grpc;
    }

    public static GrpcExecutionDetails ToGrpc(this ExecutionDetails dto)
    {
        // Identity, status, timing and error all live on the embedded summary since v0.0.10;
        // the response itself carries only the summary, the snapshotted bundle and the input.
        var summary = new GrpcExecutionSummary
        {
            Id = dto.Id.ToString(),
            Status = dto.Status.ToGrpc(),
            Workflow = dto.Workflow.ToProto(dto.WorkflowDigest),
            CreatedAt = Timestamp.FromDateTime(dto.CreatedAt.ToUniversalTime())
        };

        if (dto.ScheduledAt.HasValue)
            summary.ScheduledAt = Timestamp.FromDateTime(dto.ScheduledAt.Value.ToUniversalTime());

        if (dto.StartedAt.HasValue)
            summary.StartedAt = Timestamp.FromDateTime(dto.StartedAt.Value.ToUniversalTime());

        // ExecutionSummary has a single finished_at; take whichever terminal time is set.
        var finishedAt = dto.CompletedAt ?? dto.FailedAt ?? dto.CancelledAt;
        if (finishedAt.HasValue)
            summary.FinishedAt = Timestamp.FromDateTime(finishedAt.Value.ToUniversalTime());

        if (!string.IsNullOrEmpty(dto.CancellationReason))
            summary.CancellationReason = dto.CancellationReason;

        if (!string.IsNullOrEmpty(dto.ErrorMessage))
            summary.Error = new GrpcWorkflowError
            {
                Code = WorkflowErrorCode.ExecutionFailed,
                Message = dto.ErrorMessage
            };

        var grpc = new GrpcExecutionDetails
        {
            Summary = summary,
            Bundle = dto.Bundle.ToProto()
        };

        if (dto.Input.Count > 0)
            grpc.Input = dto.Input.ToGrpc();

        foreach (var (key, value) in dto.Env)
            grpc.Env.Add(key, value);

        return grpc;
    }

    /// <summary>
    /// Converts Dictionary to gRPC ExecutionPayload. The mapping itself lives in
    /// <see cref="ValueMapping"/>, beside the one for authored document values — a local copy is
    /// how the two ended up disagreeing about numbers.
    /// </summary>
    public static GrpcExecutionPayload? ToGrpc(this Dictionary<string, object> dictionary) =>
        dictionary.ToProto();

}
