using Google.Protobuf.WellKnownTypes;
using GrpcExecutionPayload = Utos.Daemon.V1.ExecutionPayload;

namespace Execution.Features.Extensions;

/// <summary>
/// Extension methods for converting gRPC-generated types to POCO/DTO types
/// </summary>
public static class GrpcToDtoExtensions
{
    /// <summary>
    /// Converts gRPC ExecutionPayload (protobuf Struct) to Dictionary for use in domain logic.
    /// This preserves native C# types (int, string, bool, etc.) instead of wrapping them.
    /// Returns empty dictionary if input is null or empty. Null values are filtered out.
    /// </summary>
    public static Dictionary<string, object> ToDictionary(this GrpcExecutionPayload? grpc)
    {
        if (grpc == null || grpc.Data.Count == 0)
            return new Dictionary<string, object>();

        return grpc.Data
            .Select(kvp => new { kvp.Key, Value = ConvertProtobufValue(kvp.Value) })
            .Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!);
    }

    /// <summary>
    /// Helper to convert protobuf Value to native C# object.
    /// Recursively handles nested objects and arrays.
    /// </summary>
    private static object? ConvertProtobufValue(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => value.NumberValue,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(
                kvp => kvp.Key,
                kvp => ConvertProtobufValue(kvp.Value)),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ConvertProtobufValue).ToList(),
            _ => null
        };
    }
}
