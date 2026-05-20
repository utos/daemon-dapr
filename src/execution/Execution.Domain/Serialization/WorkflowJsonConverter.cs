using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Utos.Workflow.V1;

namespace Execution.Domain.Serialization;

/// <summary>
/// Generic System.Text.Json converter for any protobuf IMessage type.
/// Uses Google's JsonFormatter/JsonParser for correct (de)serialization.
/// Apply via [JsonConverter(typeof(ProtobufJsonConverter&lt;T&gt;))] on properties/records.
/// </summary>
public class ProtobufJsonConverter<T> : JsonConverter<T> where T : IMessage, new()
{
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default);
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default);

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return default;

        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        var jsonString = jsonDoc.RootElement.GetRawText();

        return Parser.Parse<T>(jsonString);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var json = Formatter.Format(value);

        using var jsonDoc = JsonDocument.Parse(json);
        jsonDoc.RootElement.WriteTo(writer);
    }
}

// Convenience aliases for backward compatibility
public class WorkflowJsonConverter : ProtobufJsonConverter<Workflow>;

public class WorkflowBundleJsonConverter : ProtobufJsonConverter<WorkflowBundle>;
