using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.SeedWork;

/// <summary>
/// Reads and writes <see cref="ExecutionId"/> as the bare GUID string a raw <see cref="Guid"/>
/// serialized to, so events already in the stream and Dapr payloads already in flight keep
/// deserializing across the change.
/// </summary>
public sealed class ExecutionIdJsonConverter : JsonConverter<ExecutionId>
{
    public override ExecutionId Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) =>
        new(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, ExecutionId value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override ExecutionId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) =>
        new(Guid.Parse(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, ExecutionId value,
        JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value.ToString());
}
