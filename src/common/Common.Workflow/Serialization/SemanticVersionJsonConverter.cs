using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Workflow.Serialization;

/// <summary>
/// Reads and writes <see cref="SemanticVersion"/> as the plain version string, so a version
/// occupies the same JSON a raw <c>string</c> did before it became a type.
/// </summary>
public sealed class SemanticVersionJsonConverter : JsonConverter<SemanticVersion>
{
    public override SemanticVersion Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                $"Expected a semantic version string but found {reader.TokenType}.");

        var version = reader.GetString();

        return SemanticVersion.TryParse(version, out var parsed)
            ? parsed
            : throw new JsonException($"'{version}' is not a valid semantic version.");
    }

    public override void Write(Utf8JsonWriter writer, SemanticVersion value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override SemanticVersion ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var version = reader.GetString();

        return SemanticVersion.TryParse(version, out var parsed)
            ? parsed
            : throw new JsonException($"'{version}' is not a valid semantic version.");
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SemanticVersion value,
        JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString());
}
