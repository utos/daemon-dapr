using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Common.SeedWork;

namespace Common.Infrastructure.EventStore;

/// <summary>
/// The event store's wire format: a stored name, a schema version in the metadata, and a JSON
/// body.
///
/// The body is serialized with the runtime type and <b>no options</b>, deliberately. The shape
/// guarantee rides on the <c>[JsonConverter]</c> attributes the types carry, so that the same
/// shape is produced by anything that serializes them — see the persistence invariants in
/// CLAUDE.md, and <c>EventJsonShapeTests</c>, which pins it as text.
///
/// What is <i>not</i> the CLR type's business is the name. It used to be: the stored name was the
/// type's <c>AssemblyQualifiedName</c> and reading one back was <c>Type.GetType</c>, so a rename,
/// a namespace move or an assembly move made every event written before it unreadable — and
/// unreadable meant dropped, not failed. The name now comes from <see cref="EventTypeAttribute"/>
/// through <see cref="EventTypeRegistry"/> and survives all three.
/// </summary>
public class EventSerializer(
    EventTypeRegistry typeRegistry,
    EventUpcasterRegistry upcasterRegistry) : IEventSerializer
{
    /// <summary>
    /// The metadata key holding the schema version. Short because it is written on every event
    /// in every stream.
    /// </summary>
    private const string VersionKey = "v";

    public SerializedEvent SerializeEvent(IDomainEvent domainEvent)
    {
        var type = domainEvent.GetType();

        var eventData = JsonSerializer.SerializeToUtf8Bytes(domainEvent, type);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, int> { [VersionKey] = typeRegistry.CurrentVersionOf(type) });

        return new SerializedEvent(typeRegistry.NameOf(type), eventData, metadata);
    }

    public IDomainEvent DeserializeEvent(SerializedEvent serializedEvent) =>
        DeserializeEvent(serializedEvent.EventType, serializedEvent.Data, serializedEvent.Metadata);

    public IDomainEvent DeserializeEvent(string eventType, byte[] eventData, byte[] metadata)
    {
        if (!typeRegistry.TryResolve(eventType, out var type))
            throw new EventDeserializationException(
                $"No event type is registered under the name '{eventType}'. Either the event was " +
                "written by a version of the daemon that declared a name this one does not know, or " +
                "a name was changed without an upcast path.");

        var version = ReadVersion(metadata);
        var current = typeRegistry.CurrentVersionOf(type);

        if (version > current)
            throw new EventDeserializationException(
                $"Event '{eventType}' was written at schema version {version}, but this daemon only " +
                $"knows up to version {current}. It was written by a newer daemon; there is no " +
                "downcasting.");

        var json = Encoding.UTF8.GetString(eventData);

        // Only an event that needs upcasting goes through JsonNode. An event already at the
        // current version — every event, until the first schema change — is deserialized straight
        // from its text, which is both the shape EventJsonShapeTests pins and one fewer parse.
        if (version < current)
            json = Upcast(eventType, Parse(eventType, json), version, current).ToJsonString();

        try
        {
            // The null check is not ceremony: a body of literal "null" deserializes to null, and
            // casting null succeeds, so without it this method returns null despite promising not
            // to — and the readers no longer check.
            return JsonSerializer.Deserialize(json, type) as IDomainEvent
                   ?? throw new EventDeserializationException(
                       $"Event '{eventType}' deserialized to nothing.");
        }
        // Not just JsonException: a converter is free to throw whatever it likes, and a caller
        // deciding whether to fail a load or park an event should have one type to look for. The
        // filter lets our own out unwrapped rather than nesting it in itself.
        catch (Exception ex) when (ex is not EventDeserializationException)
        {
            throw new EventDeserializationException(
                $"Event '{eventType}' does not deserialize into {type.FullName}.", ex);
        }
    }

    private JsonObject Upcast(string eventType, JsonObject payload, int from, int to)
    {
        for (var version = from; version < to; version++)
        {
            if (!upcasterRegistry.TryGet(eventType, version, out var upcaster))
                throw new EventDeserializationException(
                    $"Event '{eventType}' is stored at schema version {version} and the current " +
                    $"version is {to}, but no upcaster claims v{version} -> v{version + 1}. " +
                    "Every step of the chain must be registered.");

            payload = upcaster.Upcast(payload);
        }

        return payload;
    }

    private static JsonObject Parse(string eventType, string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                   ?? throw new EventDeserializationException(
                       $"Event '{eventType}' is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new EventDeserializationException($"Event '{eventType}' is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// The schema version the event was written at. Absent, empty or unreadable metadata reads as
    /// version 1 rather than failing: the version is a claim about the body, and the body of a
    /// v1 event is exactly what an untagged one holds.
    /// </summary>
    private static int ReadVersion(byte[] metadata)
    {
        if (metadata.Length == 0) return 1;

        try
        {
            using var document = JsonDocument.Parse(metadata);

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(VersionKey, out var value) &&
                   value.TryGetInt32(out var version)
                ? version
                : 1;
        }
        catch (JsonException)
        {
            return 1;
        }
    }
}
