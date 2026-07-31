using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Common.Workflow;
using Common.Workflow.Proto;
using Execution.Features.Models;
using DomainExecutionStatus = Execution.Domain.Aggregates.ExecutionStatus;
using GrpcExecution = Utos.Daemon.V1.ScheduleExecutionResponse;
using GrpcExecutionSummary = Utos.Daemon.V1.ExecutionSummary;
using GrpcExecutionDetails = Utos.Daemon.V1.GetExecutionResponse;
using GrpcExecutionStatus = Utos.Daemon.V1.ExecutionStatus;
using GrpcWorkflow = Utos.Workflow.V1.Workflow;
using GrpcWorkflowSpec = Utos.Workflow.V1.WorkflowSpec;
using GrpcWorkflowActivity = Utos.Workflow.V1.WorkflowActivity;
using GrpcHttpActivityConfig = Utos.Workflow.V1.HttpActivityConfig;
using GrpcWorkflowMetadata = Utos.Workflow.V1.WorkflowMetadata;
using GrpcExecutionPayload = Utos.Daemon.V1.ExecutionPayload;
using GrpcTransitionTarget = Utos.Workflow.V1.TransitionTarget;
using GrpcWorkflowReference = Utos.Daemon.V1.WorkflowReference;
using GrpcWorkflowError = Utos.Workflow.V1.WorkflowError;

namespace Execution.Features.Extensions;

/// <summary>
/// Extension methods for converting POCO/DTO types to gRPC-generated types
/// </summary>
public static class DtoToGrpcExtensions
{
    /// <summary>
    /// Coarse code carried on <see cref="GrpcWorkflowError"/> until the execution pipeline
    /// records structured failure causes of its own.
    /// </summary>
    private const string ExecutionFailedCode = "EXECUTION_FAILED";

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
            _ => GrpcExecutionStatus.Unspecified
        };
    }

    /// <summary>
    /// Builds a <see cref="GrpcWorkflowReference"/> from the stored identity, leaving the
    /// optional registry/namespace/digest fields unset when absent (same "only-if-set"
    /// pattern as <c>WorkflowIdentity.ReferenceFor</c>).
    /// </summary>
    private static GrpcWorkflowReference BuildReference(string name, string version,
        string? @namespace, string? registry, string? digest)
    {
        var reference = new GrpcWorkflowReference
        {
            Name = name,
            Version = version
        };

        if (!string.IsNullOrEmpty(@namespace))
            reference.Namespace = @namespace;

        if (!string.IsNullOrEmpty(registry))
            reference.Registry = registry;

        if (!string.IsNullOrEmpty(digest))
            reference.Digest = digest;

        return reference;
    }

    public static GrpcExecutionSummary ToGrpc(this ExecutionSummary dto)
    {
        var grpc = new GrpcExecutionSummary
        {
            Id = dto.Id.ToString(),
            Status = dto.Status.ToGrpc(),
            Workflow = BuildReference(dto.WorkflowName, dto.WorkflowVersion,
                dto.WorkflowNamespace, dto.WorkflowRegistry, dto.WorkflowDigest),
            CreatedAt = Timestamp.FromDateTime(dto.CreatedAt.ToUniversalTime())
        };

        if (dto.ScheduledAt.HasValue)
            grpc.ScheduledAt = Timestamp.FromDateTime(dto.ScheduledAt.Value.ToUniversalTime());

        if (dto.StartedAt.HasValue)
            grpc.StartedAt = Timestamp.FromDateTime(dto.StartedAt.Value.ToUniversalTime());

        if (dto.FinishedAt.HasValue)
            grpc.FinishedAt = Timestamp.FromDateTime(dto.FinishedAt.Value.ToUniversalTime());

        if (!string.IsNullOrEmpty(dto.ErrorMessage))
            grpc.Error = new GrpcWorkflowError
            {
                Code = ExecutionFailedCode,
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
            Workflow = BuildReference(dto.WorkflowName, dto.WorkflowVersion,
                dto.WorkflowNamespace, dto.WorkflowRegistry, dto.WorkflowDigest),
            CreatedAt = Timestamp.FromDateTime(dto.CreatedAt.ToUniversalTime())
        };

        if (dto.ScheduledAt.HasValue)
            summary.ScheduledAt = Timestamp.FromDateTime(dto.ScheduledAt.Value.ToUniversalTime());

        if (dto.StartedAt.HasValue)
            summary.StartedAt = Timestamp.FromDateTime(dto.StartedAt.Value.ToUniversalTime());

        // ExecutionSummary has a single finished_at; take whichever terminal time is set.
        var finishedAt = dto.CompletedAt ?? dto.FailedAt;
        if (finishedAt.HasValue)
            summary.FinishedAt = Timestamp.FromDateTime(finishedAt.Value.ToUniversalTime());

        if (!string.IsNullOrEmpty(dto.ErrorMessage))
            summary.Error = new GrpcWorkflowError
            {
                Code = ExecutionFailedCode,
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
    /// Converts Dictionary to gRPC ExecutionPayload (protobuf Struct)
    /// </summary>
    public static GrpcExecutionPayload? ToGrpc(this Dictionary<string, object> dictionary)
    {
        if (dictionary.Count == 0)
            return null;

        var grpc = new GrpcExecutionPayload();
        foreach (var (key, value) in dictionary) grpc.Data.Add(key, ConvertToProtobufValue(value));

        return grpc;
    }

    /// <summary>
    /// Helper to convert C# object to protobuf Value.
    /// Handles native types, JsonElement (from System.Text.Json deserialization),
    /// nested dictionaries, and lists.
    /// </summary>
    private static Value ConvertToProtobufValue(object? value)
    {
        if (value == null)
            return Value.ForNull();

        // Handle JsonElement (when Dapr/System.Text.Json deserializes)
        if (value is JsonElement jsonElement)
            return jsonElement.ValueKind switch
            {
                JsonValueKind.Null => Value.ForNull(),
                JsonValueKind.True => Value.ForBool(true),
                JsonValueKind.False => Value.ForBool(false),
                JsonValueKind.Number => Value.ForNumber(jsonElement.GetDouble()),
                JsonValueKind.String => Value.ForString(jsonElement.GetString() ?? ""),
                JsonValueKind.Array => Value.ForList(jsonElement.EnumerateArray()
                    .Select(e => ConvertToProtobufValue(e))
                    .ToArray()),
                JsonValueKind.Object => Value.ForStruct(new Struct
                {
                    Fields =
                    {
                        jsonElement.EnumerateObject()
                            .ToDictionary(p => p.Name, p => ConvertToProtobufValue(p.Value))
                    }
                }),
                _ => Value.ForNull()
            };

        // Handle native C# types
        return value switch
        {
            string s => Value.ForString(s),
            bool b => Value.ForBool(b),
            int i => Value.ForNumber(i),
            long l => Value.ForNumber(l),
            double d => Value.ForNumber(d),
            float f => Value.ForNumber(f),
            decimal dec => Value.ForNumber((double)dec),
            ValueMap map => Value.ForStruct(map.ToProto()),
            IDictionary<string, object?> dict => Value.ForStruct(new Struct
            {
                Fields = { dict.ToDictionary(kvp => kvp.Key, kvp => ConvertToProtobufValue(kvp.Value)) }
            }),
            IEnumerable<object?> list => Value.ForList(list.Select(ConvertToProtobufValue).ToArray()),
            _ => Value.ForString(value.ToString() ?? "")
        };
    }
}
