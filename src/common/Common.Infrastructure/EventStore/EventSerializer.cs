using System.Text.Json;
using Common.SeedWork;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.EventStore;

public class EventSerializer(ILogger<EventSerializer> logger) : IEventSerializer
{
    public SerializedEvent SerializeEvent(IDomainEvent domainEvent)
    {
        var eventType = domainEvent.GetType().AssemblyQualifiedName!;
        var eventData = JsonSerializer.SerializeToUtf8Bytes(domainEvent, domainEvent.GetType());
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { });

        return new SerializedEvent(eventType, eventData, metadata);
    }

    public IDomainEvent? DeserializeEvent(SerializedEvent serializedEvent)
    {
        return DeserializeEvent(serializedEvent.EventType, serializedEvent.Data, serializedEvent.Metadata);
    }

    public IDomainEvent? DeserializeEvent(string eventType, byte[] eventData, byte[] metadata)
    {
        try
        {
            var type = Type.GetType(eventType);

            if (type == null)
            {
                logger.LogWarning("Unknown event type: {EventType}", eventType);
                return null;
            }

            var json = System.Text.Encoding.UTF8.GetString(eventData);
            return (IDomainEvent)JsonSerializer.Deserialize(json, type)!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize event {EventType}", eventType);
            return null;
        }
    }
}

// /// <summary>
// /// Custom JSON converter that handles protobuf IMessage types using protobuf's JsonFormatter/JsonParser
// /// </summary>
// public class ProtobufJsonConverter : JsonConverterFactory
// {
//     public override bool CanConvert(Type typeToConvert)
//     {
//         return typeof(IMessage).IsAssignableFrom(typeToConvert);
//     }
//
//     public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
//     {
//         return (JsonConverter)Activator.CreateInstance(
//             typeof(ProtobufJsonConverterInner<>).MakeGenericType(typeToConvert))!;
//     }
//
//     private class ProtobufJsonConverterInner<T> : JsonConverter<T> where T : IMessage, new()
//     {
//         private static readonly JsonParser Parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
//         private static readonly JsonFormatter Formatter = new JsonFormatter(JsonFormatter.Settings.Default);
//
//         public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
//         {
//             var json = JsonDocument.ParseValue(ref reader).RootElement.GetRawText();
//             return Parser.Parse<T>(json);
//         }
//
//         public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
//         {
//             var json = Formatter.Format(value);
//             using var doc = JsonDocument.Parse(json);
//             doc.WriteTo(writer);
//         }
//     }
// }
