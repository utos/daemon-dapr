using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Execution.Infrastructure.Extensions;

/// <summary>
/// Extension methods for working with protobuf-generated types.
/// Provides convenient conversions from proto types to C# types.
/// </summary>
public static class ProtoExtensions
{
    /// <summary>
    /// Converts a protobuf Value to a native C# object.
    /// Handles primitives, nested objects, and arrays.
    /// </summary>
    public static object? ToObject(this Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => ConvertNumber(value.NumberValue),
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.StructValue => value.StructValue.ToDict(),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(v => v.ToObject()).ToList(),
            _ => null
        };
    }

    /// <summary>
    /// Converts a protobuf Struct to a Dictionary&lt;string, object&gt;.
    /// Recursively converts nested values.
    /// </summary>
    public static Dictionary<string, object> ToDict(this Struct protoStruct)
    {
        return protoStruct.Fields
            .Select(kvp => new { kvp.Key, Value = kvp.Value.ToObject() })
            .Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!);
    }

    /// <summary>
    /// Converts a protobuf MapField to a Dictionary&lt;string, string&gt;.
    /// Common use case for HTTP headers.
    /// </summary>
    public static Dictionary<string, string> ToDict(this MapField<string, string> protoMap)
    {
        return new Dictionary<string, string>(protoMap);
    }

    /// <summary>
    /// Converts a protobuf map of Values to a Dictionary&lt;string, object&gt;.
    /// </summary>
    public static Dictionary<string, object> ToDict(this MapField<string, Value> protoMap)
    {
        return protoMap
            .Select(kvp => new { kvp.Key, Value = kvp.Value.ToObject() })
            .Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value!);
    }

    /// <summary>
    /// Converts a double number value to the most appropriate numeric type.
    /// Returns int if the value is a whole number, otherwise double.
    /// </summary>
    private static object ConvertNumber(double value)
    {
        // If it's a whole number and within int range, return as int
        if (value == Math.Floor(value) && value >= int.MinValue && value <= int.MaxValue)
            return (int)value;

        // If it's a whole number but outside int range, return as long
        if (value == Math.Floor(value) && value >= long.MinValue && value <= long.MaxValue)
            return (long)value;

        // Otherwise return as double
        return value;
    }
}
