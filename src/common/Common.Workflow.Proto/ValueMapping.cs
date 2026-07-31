using Google.Protobuf.WellKnownTypes;

namespace Common.Workflow.Proto;

/// <summary>
/// Translates between <c>google.protobuf.Struct</c>/<c>Value</c> and the domain
/// <see cref="ValueMap"/>. Kept separate from the document mapping because the execution edge
/// needs it on its own, for <c>ExecutionPayload</c>.
/// </summary>
public static class ValueMapping
{
    public static ValueMap ToDomain(this Struct source) =>
        new(source.Fields.ToDictionary(field => field.Key, field => field.Value.ToDomain()));

    public static object? ToDomain(this Value source)
    {
        return source.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => FromNumber(source.NumberValue),
            Value.KindOneofCase.StringValue => source.StringValue,
            Value.KindOneofCase.BoolValue => source.BoolValue,
            Value.KindOneofCase.StructValue => source.StructValue.ToDomain(),
            Value.KindOneofCase.ListValue => source.ListValue.Values.Select(ToDomain).ToList(),
            _ => null
        };
    }

    public static Struct ToProto(this ValueMap source)
    {
        var result = new Struct();

        foreach (var (key, value) in source.Fields)
            result.Fields[key] = ToProtoValue(value);

        return result;
    }

    public static Value ToProtoValue(object? value)
    {
        switch (value)
        {
            case null:
                return Value.ForNull();

            case string s:
                return Value.ForString(s);

            case bool b:
                return Value.ForBool(b);

            case int i:
                return Value.ForNumber(i);

            case long l:
                return Value.ForNumber(l);

            case double d:
                return Value.ForNumber(d);

            case float f:
                return Value.ForNumber(f);

            case decimal m:
                return Value.ForNumber((double)m);

            case ValueMap map:
                return Value.ForStruct(map.ToProto());

            case IReadOnlyDictionary<string, object?> nested:
                return Value.ForStruct(new ValueMap(nested).ToProto());

            // Ahead of IEnumerable: strings and dictionaries are enumerable too, and are
            // already handled above.
            case System.Collections.IEnumerable items:
                return Value.ForList(items.Cast<object?>().Select(ToProtoValue).ToArray());

            default:
                return Value.ForString(value.ToString() ?? string.Empty);
        }
    }

    /// <summary>
    /// Proto carries every number as a double. Narrowing whole numbers back to int/long keeps
    /// template arithmetic and comparisons behaving as authored — a counter incremented across
    /// a loop back-edge must stay an integer rather than drifting to "1.0".
    /// </summary>
    private static object FromNumber(double value)
    {
        if (value == Math.Floor(value) && !double.IsInfinity(value))
        {
            if (value is >= int.MinValue and <= int.MaxValue)
                return (int)value;

            if (value is >= long.MinValue and <= long.MaxValue)
                return (long)value;
        }

        return value;
    }
}
