using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Workflow.Serialization;

/// <summary>
/// Deserializes a <c>Dictionary&lt;string, object&gt;</c> whose values are real CLR types
/// rather than <see cref="JsonElement"/>.
///
/// <see cref="ObjectDictionaryJsonConverter"/> does this for a property declared as
/// <c>object</c>; a <see cref="JsonConverter{T}"/> named in a property annotation has to
/// match that property's declared type, so a dictionary-typed property needs this one.
///
/// Apply via <c>[property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]</c>
/// on any payload crossing a Dapr activity boundary, and on anything persisted whose values
/// a template later reads.
/// </summary>
public sealed class StringObjectDictionaryJsonConverter : JsonConverter<Dictionary<string, object>>
{
    // The ambient options are deliberately dropped in favour of WorkflowJson.Options. The caller
    // that matters most, EventSerializer, serializes with the defaults and no converters —
    // honouring it would put JsonElements straight back into a payload whose whole purpose is
    // not to have any. The guarantee has to hold regardless of who is serializing.
    //
    // WorkflowJson.Options also deliberately does not name this converter, so there is no
    // recursion back into it for the very dictionary type it handles.

    public override Dictionary<string, object>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        // Read key by key rather than handing the object to System.Text.Json, which would write and
        // read *these* keys itself and so skip the escaping of `$utos:…` that makes the blob
        // encoding unambiguous. Nested values still go through the other converter.
        var map = ObjectDictionaryJsonConverter.ReadMap(ref reader);

        return map.ToDictionary(entry => entry.Key, entry => entry.Value!);
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
    {
        // Cast rather than copied: nullability is erased at run time, so this dictionary already
        // *is* the interface the writer wants, and this is on the path of every activity input.
        ObjectDictionaryJsonConverter.WriteMap(
            writer, (IDictionary<string, object?>)value, WorkflowJson.Options);
    }
}
