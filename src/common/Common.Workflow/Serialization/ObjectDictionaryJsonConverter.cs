using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Workflow.Serialization;

/// <summary>
/// Reads an <c>object</c>-typed value as a real CLR type rather than a <see cref="JsonElement"/>,
/// which template expressions cannot do arithmetic, comparisons or boolean tests on.
/// </summary>
/// <remarks>
/// Nested objects become <c>Dictionary&lt;string, object?&gt;</c>, deliberately not
/// <see cref="ValueMap"/>: these are run values that reach a template scope directly, where the
/// scope converter copies dictionaries and lists into plain JavaScript objects and arrays — one
/// shape for every run value. <see cref="ValueMapJsonConverter"/> nests as a ValueMap for the
/// opposite reason: an authored document is flattened by the renderer before it can reach a
/// scope.
/// </remarks>
public sealed class ObjectDictionaryJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadValue(ref reader);
    }

    /// <summary>
    /// Identical to what System.Text.Json does for an <c>object</c> by default — both dispatch on
    /// the runtime type. Only the read path needed changing.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => NumberNarrowing.Read(ref reader),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.StartObject => ReadObject(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
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
            dictionary[propertyName] = ReadValue(ref reader);
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

            list.Add(ReadValue(ref reader));
        }

        throw new JsonException("Unexpected end of JSON while reading array");
    }
}
