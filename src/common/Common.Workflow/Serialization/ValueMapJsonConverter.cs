using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Workflow.Serialization;

/// <summary>
/// Reads and writes a <see cref="ValueMap"/> as a bare JSON object whose leaves are real CLR
/// types rather than <see cref="JsonElement"/>. Template expressions have to do arithmetic,
/// comparisons and boolean tests on these values, which a JsonElement does not support.
/// </summary>
public sealed class ValueMapJsonConverter : JsonConverter<ValueMap>
{
    public override ValueMap? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected an object for {nameof(ValueMap)}, found {reader.TokenType}.");

        return new ValueMap(ReadObject(ref reader));
    }

    public override void Write(Utf8JsonWriter writer, ValueMap value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var (key, item) in value.Fields)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, item, options);
        }

        writer.WriteEndObject();
    }

    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            // Nested objects stay ValueMaps, matching what mapping from the wire format
            // produces — the renderer and the templating scope switch on the type.
            JsonTokenType.StartObject => new ValueMap(ReadObject(ref reader)),
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    private static object ReadNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt32(out var intValue))
            return intValue;

        if (reader.TryGetInt64(out var longValue))
            return longValue;

        return reader.GetDouble();
    }

    private static Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader)
    {
        var fields = new Dictionary<string, object?>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return fields;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name");

            var propertyName = reader.GetString()!;

            reader.Read();
            fields[propertyName] = ReadValue(ref reader);
        }

        throw new JsonException("Unexpected end of JSON while reading object");
    }

    private static List<object?> ReadArray(ref Utf8JsonReader reader)
    {
        var items = new List<object?>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return items;

            items.Add(ReadValue(ref reader));
        }

        throw new JsonException("Unexpected end of JSON while reading array");
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            // Nested maps arrive either already wrapped or as the raw dictionary a renderer
            // produced; both write as a plain JSON object.
            case ValueMap map:
                new ValueMapJsonConverter().Write(writer, map, options);
                break;

            case IReadOnlyDictionary<string, object?> nested:
                new ValueMapJsonConverter().Write(writer, new ValueMap(nested), options);
                break;

            case string s:
                writer.WriteStringValue(s);
                break;

            // Ahead of IEnumerable: a string is enumerable too, and so is a dictionary.
            case System.Collections.IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                    WriteValue(writer, item, options);
                writer.WriteEndArray();
                break;

            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
    }
}
