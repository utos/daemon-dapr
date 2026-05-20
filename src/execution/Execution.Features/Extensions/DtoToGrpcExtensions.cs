using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using Execution.Features.Models;
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
using GrpcWorkflowApiVersion = Utos.Workflow.V1.WorkflowApiVersion;
using GrpcWorkflowKind = Utos.Workflow.V1.WorkflowKind;
using GrpcTransitionTarget = Utos.Workflow.V1.TransitionTarget;

namespace Execution.Features.Extensions;

/// <summary>
/// Extension methods for converting POCO/DTO types to gRPC-generated types
/// </summary>
public static class DtoToGrpcExtensions
{
    public static GrpcExecutionSummary ToGrpc(this ExecutionSummary dto)
    {
        var grpc = new GrpcExecutionSummary
        {
            Id = dto.Id.ToString(),
            Status = dto.Status,
            WorkflowName = dto.WorkflowName,
            WorkflowVersion = dto.WorkflowVersion,
            CreatedAt = Timestamp.FromDateTime(dto.CreatedAt.ToUniversalTime())
        };

        if (dto.ScheduledAt.HasValue)
            grpc.ScheduledAt = Timestamp.FromDateTime(dto.ScheduledAt.Value.ToUniversalTime());

        if (dto.StartedAt.HasValue)
            grpc.StartedAt = Timestamp.FromDateTime(dto.StartedAt.Value.ToUniversalTime());

        if (dto.FinishedAt.HasValue)
            grpc.FinishedAt = Timestamp.FromDateTime(dto.FinishedAt.Value.ToUniversalTime());

        if (!string.IsNullOrEmpty(dto.ErrorMessage))
            grpc.ErrorMessage = dto.ErrorMessage;

        return grpc;
    }

    public static GrpcExecutionDetails ToGrpc(this ExecutionDetails dto)
    {
        var grpc = new GrpcExecutionDetails
        {
            Id = dto.Id.ToString(),
            Status = dto.Status,
            WorkflowName = dto.WorkflowName,
            WorkflowVersion = dto.WorkflowVersion,
            Bundle = dto.Bundle,
            CreatedAt = Timestamp.FromDateTime(dto.CreatedAt.ToUniversalTime())
        };

        if (dto.Input.Count > 0)
            grpc.Input = dto.Input.ToGrpc();

        if (dto.ScheduledAt.HasValue)
            grpc.ScheduledAt = Timestamp.FromDateTime(dto.ScheduledAt.Value.ToUniversalTime());

        if (dto.StartedAt.HasValue)
            grpc.StartedAt = Timestamp.FromDateTime(dto.StartedAt.Value.ToUniversalTime());

        if (dto.CompletedAt.HasValue)
            grpc.CompletedAt = Timestamp.FromDateTime(dto.CompletedAt.Value.ToUniversalTime());

        if (dto.FailedAt.HasValue)
            grpc.FailedAt = Timestamp.FromDateTime(dto.FailedAt.Value.ToUniversalTime());

        if (!string.IsNullOrEmpty(dto.ErrorMessage))
            grpc.ErrorMessage = dto.ErrorMessage;

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
            IDictionary<string, object?> dict => Value.ForStruct(new Struct
            {
                Fields = { dict.ToDictionary(kvp => kvp.Key, kvp => ConvertToProtobufValue(kvp.Value)) }
            }),
            IEnumerable<object?> list => Value.ForList(list.Select(ConvertToProtobufValue).ToArray()),
            _ => Value.ForString(value.ToString() ?? "")
        };
    }
}
