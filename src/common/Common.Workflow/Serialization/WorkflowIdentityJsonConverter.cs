using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Workflow.Serialization;

/// <summary>
/// Reads and writes <see cref="WorkflowIdentity"/> as its canonical
/// <c>[registry/][namespace/]name:version</c> string, which is also the key format a
/// <see cref="WorkflowBundle"/> uses.
/// </summary>
public sealed class WorkflowIdentityJsonConverter : JsonConverter<WorkflowIdentity>
{
    public override WorkflowIdentity Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(
                $"Expected a workflow identity string but found {reader.TokenType}.");

        return ParseOrThrow(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, WorkflowIdentity value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    public override WorkflowIdentity ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) =>
        ParseOrThrow(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, WorkflowIdentity value,
        JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString());

    private static WorkflowIdentity ParseOrThrow(string? identity) =>
        WorkflowIdentity.TryParse(identity, out var parsed)
            ? parsed
            : throw new JsonException($"'{identity}' is not a valid workflow identity.");
}
