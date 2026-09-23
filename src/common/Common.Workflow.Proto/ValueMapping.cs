using System.Text.Json;
using Common.Workflow.Serialization;
using Google.Protobuf.WellKnownTypes;
using Utos.Workflows.V1;
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
            result.Data.Add(key, ToWorkflowValue(value));

        return result;
    }

    // ---- Run data: the typed value the daemon wire carries since spec 0.20 -------------------

    /// <summary>
    /// A <c>WorkflowValue</c> as the interpreter holds it. The same scalar rules as every other
    /// door — a number narrows by value, because this one is a bare double too — plus the kind
    /// that has no <c>google.protobuf.Value</c> equivalent at all: a blob.
    /// <para>
    /// The kinds are a closed set and an unset one is refused rather than read as null. A value
    /// with no kind is <c>UTOS-V101</c>, and treating it as null would hand a run a missing field
    /// where a client sent a malformed one.
    /// </para>
    /// </summary>
    private static object? ToRuntimeValue(WorkflowValue source)
    {
        return source.KindCase switch
        {
            WorkflowValue.KindOneofCase.NullValue => null,
            WorkflowValue.KindOneofCase.NumberValue => Number(source.NumberValue),
            WorkflowValue.KindOneofCase.StringValue => source.StringValue,
            WorkflowValue.KindOneofCase.BoolValue => source.BoolValue,

            WorkflowValue.KindOneofCase.MapValue => source.MapValue.Fields.ToDictionary(
                field => field.Key,
                field => ToRuntimeValue(field.Value)),

            WorkflowValue.KindOneofCase.ListValue =>
                source.ListValue.Values.Select(ToRuntimeValue).ToList(),

            WorkflowValue.KindOneofCase.BlobValue => source.BlobValue.ToDomain(),

            _ => throw new InvalidOperationException(
                "A workflow value must have exactly one kind set (UTOS-V101).")
        };
    }

    public static WorkflowBlob ToDomain(this Utos.Workflows.V1.Blob source) =>
        new(source.BackingCase == Utos.Workflows.V1.Blob.BackingOneofCase.Id ? source.Id : null,
            source.BackingCase == Utos.Workflows.V1.Blob.BackingOneofCase.Data
                ? source.Data.ToByteArray()
                : null,
            source.Offset,
            source.Size,
            source.MediaType,
            source.HasName ? source.Name : null,
            source.LastModified?.ToDateTimeOffset());

    public static Utos.Workflows.V1.Blob ToProto(this WorkflowBlob source)
    {
        var result = new Utos.Workflows.V1.Blob
        {
            Offset = source.Offset,
            Size = source.Size,
            MediaType = source.MediaType
        };

        if (source.Data is not null)
            result.Data = Google.Protobuf.ByteString.CopyFrom(source.Data);
        else if (source.Id is not null)
            result.Id = source.Id;

        if (source.Name is not null)
            result.Name = source.Name;

        if (source.LastModified is { } modified)
            result.LastModified = Timestamp.FromDateTimeOffset(modified);

        return result;
    }

    /// <summary>
    /// The outbound half of <see cref="ToRuntimeValue(WorkflowValue)"/>. Shares every scalar rule
    /// with <see cref="ToProtoValue"/> — the two doors must agree about what 42 is — and differs
    /// only in the two kinds the typed value adds: a blob travels as itself, and nothing is
    /// stringified.
    /// </summary>
    public static WorkflowValue ToWorkflowValue(object? value)
    {
        switch (value)
        {
            case null:
                return new WorkflowValue { NullValue = NullValue.NullValue };

            case string s:
                return new WorkflowValue { StringValue = s };

            case bool b:
                return new WorkflowValue { BoolValue = b };

            case int i:
                return new WorkflowValue { NumberValue = i };

            case long l:
                return new WorkflowValue { NumberValue = l };

            case double d:
                return new WorkflowValue { NumberValue = d };

            case float f:
                return new WorkflowValue { NumberValue = f };

            case decimal m:
                return new WorkflowValue { NumberValue = (double)m };

            case WorkflowBlob blob:
                return new WorkflowValue { BlobValue = blob.ToProto() };

            // An HTTP activity's response body is parsed without converters and stays a
            // JsonElement until it crosses a Dapr boundary, so one can still reach here.
            case JsonElement json:
                return WorkflowValueFromJson(json);

            case ValueMap map:
                return ToWorkflowMapValue(map.Fields);

            case IReadOnlyDictionary<string, object?> nested:
                return ToWorkflowMapValue(nested);

            case IDictionary<string, object?> nested:
                return ToWorkflowMapValue(nested.AsReadOnly());

            // Ahead of IEnumerable, for the reason ToProtoValue gives.
            case System.Collections.IEnumerable items:
            {
                var list = new WorkflowList();
                foreach (var item in items) list.Values.Add(ToWorkflowValue(item));
                return new WorkflowValue { ListValue = list };
            }

            default:
                throw new InvalidOperationException(
                    $"No proto mapping for {value.GetType()}.");
        }
    }

    public static WorkflowMap ToWorkflowMap(this IReadOnlyDictionary<string, object?> source)
    {
        var result = new WorkflowMap();

        foreach (var (key, value) in source)
            result.Fields[key] = ToWorkflowValue(value);

        return result;
    }

    private static WorkflowValue ToWorkflowMapValue(IReadOnlyDictionary<string, object?> source) =>
        new() { MapValue = source.ToWorkflowMap() };

    private static WorkflowValue WorkflowValueFromJson(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return new WorkflowValue { NullValue = NullValue.NullValue };

            case JsonValueKind.True:
                return new WorkflowValue { BoolValue = true };

            case JsonValueKind.False:
                return new WorkflowValue { BoolValue = false };

            case JsonValueKind.Number:
                return new WorkflowValue { NumberValue = json.GetDouble() };

            case JsonValueKind.String:
                return new WorkflowValue { StringValue = json.GetString() ?? string.Empty };

            case JsonValueKind.Array:
            {
                var list = new WorkflowList();
                foreach (var item in json.EnumerateArray()) list.Values.Add(WorkflowValueFromJson(item));
                return new WorkflowValue { ListValue = list };
            }

            case JsonValueKind.Object:
            {
                var map = new WorkflowMap();
                foreach (var property in json.EnumerateObject())
                    map.Fields[property.Name] = WorkflowValueFromJson(property.Value);
                return new WorkflowValue { MapValue = map };
            }

            default:
                return new WorkflowValue { NullValue = NullValue.NullValue };
        }
    }

    /// <summary>
    /// The same scalar rules as <see cref="ToDomain(Value)"/>, but nesting into a plain
    /// dictionary rather than a <see cref="ValueMap"/>. That difference is deliberate: run data
    /// goes straight into a template scope, and the scope converter copies dictionaries and lists
    /// into plain JavaScript objects and arrays — it accepts a ValueMap too, but a dictionary is
    /// what every other run value is, and one shape is easier to reason about than two. An
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
