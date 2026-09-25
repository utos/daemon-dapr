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
/// <para>
/// Both directions are written out since spec 0.20, because a blob is a kind of value that JSON has
/// no kind for. The write path wraps one in <see cref="WorkflowBlobJson.Key"/> and escapes any map
/// key that could be mistaken for it; the read path undoes both. Those two halves are the daemon's
/// internal encoding, and they exist in this one converter so that nothing else has to know a key
/// can be meaningful.
/// </para>
/// </remarks>
public sealed class ObjectDictionaryJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadValue(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        WriteValue(writer, value, options);
    }

    /// <summary>
    /// The write path for one run value. Blobs and maps are written here; everything else is what
    /// System.Text.Json does for an <c>object</c> by default, dispatching on the runtime type.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;

            // The wrapper is added here rather than by the converter on WorkflowBlob, because it is
            // only needed where the declared type is `object` and the reader would otherwise have
            // nothing to tell a blob from a map.
            case WorkflowBlob blob:
                WorkflowBlobJson.Write(writer, blob);
                return;

            // The run-value map shape, and the only shape that can contain a blob — so the only one
            // whose keys need escaping. Anything else dictionary-like (an env map of strings, say)
            // cannot, and is left to System.Text.Json so that its own converters still apply.
            //
            // One case covers both `Dictionary<string, object>` and `Dictionary<string, object?>`:
            // nullability is erased at run time, so they are the same type to a pattern match.
            case IDictionary<string, object?> map:
                WriteMap(writer, map, options);
                return;

            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                return;
        }
    }

    /// <summary>
    /// A run-value map. Public because <see cref="StringObjectDictionaryJsonConverter"/> handles the
    /// same shape at the top of a declared property, where System.Text.Json would otherwise write
    /// the keys itself and skip the escaping.
    /// </summary>
    public static void WriteMap(
        Utf8JsonWriter writer, IDictionary<string, object?> map, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var (key, item) in map)
        {
            writer.WritePropertyName(WorkflowBlobJson.Escape(key));
            WriteValue(writer, item, options);
        }

        writer.WriteEndObject();
    }

    public static object? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => NumberNarrowing.Read(ref reader),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.StartObject => ReadObject(ref reader, allowBlob: true),
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    /// <summary>
    /// A map, for a property whose declared type is one — where the value cannot be a blob however
    /// its keys read, so the wrapper is not looked for at this level. Nested values still are.
    /// </summary>
    public static Dictionary<string, object?> ReadMap(ref Utf8JsonReader reader) =>
        (Dictionary<string, object?>)ReadObject(ref reader, allowBlob: false)!;

    private static object? ReadObject(ref Utf8JsonReader reader, bool allowBlob)
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

            // A blob, but only as the sole property of an object whose value is an object. The
            // narrowness is deliberate: it is what the write path produces, and a map that reached
            // here with the reserved key any other way is data a workflow made and must stay data.
            if (allowBlob
                && propertyName == WorkflowBlobJson.Key
                && dictionary.Count == 0
                && reader.TokenType == JsonTokenType.StartObject)
            {
                var blob = WorkflowBlobJson.ReadBody(ref reader);

                if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
                    throw new JsonException($"'{WorkflowBlobJson.Key}' must be an object's only property.");

                return blob;
            }

            dictionary[WorkflowBlobJson.Unescape(propertyName)] = ReadValue(ref reader);
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
