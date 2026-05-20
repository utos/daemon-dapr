using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Infrastructure.Serialization;

public class ObjectDictionaryJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadValue(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
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

            JsonTokenType.StartObject => ReadObject(ref reader),

            JsonTokenType.StartArray => ReadArray(ref reader),

            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    private static object ReadNumber(ref Utf8JsonReader reader)
    {
        // Try int first (most common)
        if (reader.TryGetInt32(out var intValue))
            return intValue;

        // Try long
        if (reader.TryGetInt64(out var longValue))
            return longValue;

        // Fallback to double for decimals
        return reader.GetDouble();
    }

    private static Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader)
    {
        var dictionary = new Dictionary<string, object?>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return dictionary;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name");

            var propertyName = reader.GetString()!;

            reader.Read();
            var value = ReadValue(ref reader);

            dictionary[propertyName] = value;
        }

        throw new JsonException("Unexpected end of JSON while reading object");
    }

    private static List<object?> ReadArray(ref Utf8JsonReader reader)
    {
        var list = new List<object?>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return list;

            var value = ReadValue(ref reader);
            list.Add(value);
        }

        throw new JsonException("Unexpected end of JSON while reading array");
    }
}
