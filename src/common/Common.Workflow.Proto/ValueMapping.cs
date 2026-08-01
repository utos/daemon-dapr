using System.Text.Json;
using Common.Workflow.Serialization;
using Google.Protobuf.WellKnownTypes;
using Wire = Utos.Daemon.V1;

namespace Common.Workflow.Proto;

/// <summary>
/// Translates between proto values and the daemon's own, for both doors a value can come
/// through: a <c>google.protobuf.Struct</c> inside an authored document, and an
/// <c>ExecutionPayload</c> carrying a run's input. Both are a
/// <c>map&lt;string, google.protobuf.Value&gt;</c> underneath, and they were previously mapped by
/// two independent converters that disagreed — one narrowed whole numbers and the other did not,
/// so the same 42 was an int in a transform and a double in <c>{{ input.x }}</c>.
/// </summary>
public static class ValueMapping
{
    // ---- Documents: nested objects stay ValueMaps -------------------------------------------

    public static ValueMap ToDomain(this Struct source) =>
        new(source.Fields.ToDictionary(field => field.Key, field => field.Value.ToDomain()));

    public static object? ToDomain(this Value source)
    {
        return source.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => Number(source.NumberValue),
            Value.KindOneofCase.StringValue => source.StringValue,
            Value.KindOneofCase.BoolValue => source.BoolValue,
            Value.KindOneofCase.StructValue => source.StructValue.ToDomain(),
            Value.KindOneofCase.ListValue => source.ListValue.Values.Select(ToDomain).ToList(),
            _ => null
        };
    }

    // ---- Run data: nested objects become plain dictionaries ----------------------------------

    /// <summary>
    /// A run's input, as the interpreter holds it. A null top-level field is dropped rather than
    /// carried, because run input is a <c>Dictionary&lt;string, object&gt;</c> and a template reads
    /// a missing key and an explicit null the same way — the same rule
    /// <see cref="ValueMap.ToDictionary"/> applies.
    /// </summary>
    public static Dictionary<string, object> ToDictionary(this Wire.ExecutionPayload? source)
    {
        if (source is null || source.Data.Count == 0)
            return new Dictionary<string, object>();

        return source.Data
            .Select(field => new { field.Key, Value = ToRuntimeValue(field.Value) })
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Key, field => field.Value!);
    }

    public static Wire.ExecutionPayload? ToProto(this IReadOnlyDictionary<string, object> source)
    {
        if (source.Count == 0)
            return null;

        var result = new Wire.ExecutionPayload();

        foreach (var (key, value) in source)
            result.Data.Add(key, ToProtoValue(value));

        return result;
    }

    /// <summary>
    /// The same scalar rules as <see cref="ToDomain(Value)"/>, but nesting into a plain
    /// dictionary rather than a <see cref="ValueMap"/>. That difference is deliberate and
    /// load-bearing: run data goes straight into a template scope, and Scriban resolves members
    /// on something that implements a dictionary interface while falling back to reflection on
    /// anything else — a ValueMap here would stop <c>{{ input.user.name }}</c> resolving. An
    /// authored document has no such problem because the renderer flattens it first.
    /// </summary>
    private static object? ToRuntimeValue(Value source)
    {
        return source.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => Number(source.NumberValue),
            Value.KindOneofCase.StringValue => source.StringValue,
            Value.KindOneofCase.BoolValue => source.BoolValue,
            Value.KindOneofCase.StructValue => source.StructValue.Fields.ToDictionary(
                field => field.Key,
                field => ToRuntimeValue(field.Value)),
            Value.KindOneofCase.ListValue => source.ListValue.Values.Select(ToRuntimeValue).ToList(),
            _ => null
        };
    }

    // ---- Outbound ----------------------------------------------------------------------------

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

            // An HTTP activity's response body is parsed without converters and stays a
            // JsonElement until it crosses a Dapr boundary, so one can still reach here.
            case JsonElement json:
                return FromJsonElement(json);

            case ValueMap map:
                return Value.ForStruct(map.ToProto());

            case IReadOnlyDictionary<string, object?> nested:
                return Value.ForStruct(new ValueMap(nested).ToProto());

            case IDictionary<string, object?> nested:
                return Value.ForStruct(new ValueMap(nested.AsReadOnly()).ToProto());

            // Ahead of IEnumerable: strings and dictionaries are enumerable too, and are
            // already handled above. Non-generic on purpose — int[] and List<int> do not
            // implement IEnumerable<object?>, and matching only that emitted them as the
            // string "System.Int32[]".
            case System.Collections.IEnumerable items:
                return Value.ForList(items.Cast<object?>().Select(ToProtoValue).ToArray());

            default:
                // Stringifying an unmapped type — which this used to do — hands the client a
                // number rendered as text and no way to notice.
                throw new InvalidOperationException(
                    $"No proto mapping for {value.GetType()}.");
        }
    }

    private static Value FromJsonElement(JsonElement json) =>
        json.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => Value.ForNull(),
            JsonValueKind.True => Value.ForBool(true),
            JsonValueKind.False => Value.ForBool(false),
            JsonValueKind.Number => Value.ForNumber(json.GetDouble()),
            JsonValueKind.String => Value.ForString(json.GetString() ?? string.Empty),
            JsonValueKind.Array => Value.ForList(
                json.EnumerateArray().Select(item => (object?)item).Select(ToProtoValue).ToArray()),
            JsonValueKind.Object => Value.ForStruct(new Struct
            {
                Fields =
                {
                    json.EnumerateObject().ToDictionary(p => p.Name, p => ToProtoValue(p.Value))
                }
            }),
            _ => Value.ForNull()
        };

    /// <summary>
    /// Proto carries every number as a double, so the value is the only evidence available.
    /// Non-finite values are rejected here rather than carried inward: <c>System.Text.Json</c>
    /// refuses to write NaN or ±Infinity, and the first place it refuses is inside the read-model
    /// projection — which parks the event, leaving a run that was acknowledged to the client but
    /// never scheduled and never listed.
    /// </summary>
    private static object Number(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "Workflow values must be finite numbers.");

        return NumberNarrowing.FromDouble(value);
    }
}
