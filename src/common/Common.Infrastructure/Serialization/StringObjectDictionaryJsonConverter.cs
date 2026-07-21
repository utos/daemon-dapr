using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Infrastructure.Serialization;

/// <summary>
/// Deserializes a <c>Dictionary&lt;string, object&gt;</c> whose values are real CLR types
/// rather than <see cref="JsonElement"/>.
///
/// <see cref="ObjectDictionaryJsonConverter"/> does this for a property declared as
/// <c>object</c>; a <see cref="JsonConverter{T}"/> named in a property annotation has to
/// match that property's declared type, so a dictionary-typed property needs this one.
///
/// Apply via <c>[property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]</c>
/// on any payload crossing a Dapr activity boundary. Without it the values arrive as
/// <see cref="JsonElement"/>, which template expressions cannot do arithmetic, comparisons
/// or boolean tests on.
/// </summary>
public class StringObjectDictionaryJsonConverter : JsonConverter<Dictionary<string, object>>
{
    // Carries ObjectDictionaryJsonConverter for the `object` values. It deliberately does
    // not include this converter, so there is no recursion back into it.
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        Converters = { new ObjectDictionaryJsonConverter() }
    };

    public override Dictionary<string, object>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object>>(ref reader, ValueOptions);
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, ValueOptions);
    }
}
