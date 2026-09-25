using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Workflow.Serialization;

/// <summary>
/// The daemon's internal encoding for a blob inside a run value, and the escaping that makes it
/// unambiguous.
/// <para>
/// A blob is a <em>kind</em> of value, not a shape: spec 0.20 made that structural so that no key
/// inside a map — <c>$blob</c> or any other — decides what something is. But a run value crosses a
/// Dapr activity boundary and is written to an event stream as JSON, where the only structures
/// available are objects, arrays and scalars. So the daemon needs an encoding of its own, and the
/// spec asks for it in as many words: values as <c>WorkflowValue</c> end to end, <em>with an
/// unambiguous internal encoding</em>.
/// </para>
/// <para>
/// The encoding is a one-property object under <see cref="Key"/>. Unambiguous is the whole
/// difficulty: a workflow may legitimately produce a map containing that exact key, and reading it
/// back as a blob would let a document forge a handle. So every map key that could be mistaken for
/// the reserved one is escaped on the way out and unescaped on the way in — see
/// <see cref="Escape"/>. Nothing else in the daemon may treat a key as meaningful.
/// </para>
/// </summary>
public static class WorkflowBlobJson
{
    /// <summary>
    /// The reserved key. Namespaced and punctuated so that it is not a plausible field name, but
    /// that is a convenience — correctness rests on the escaping, not on the key being unlikely.
    /// </summary>
    public const string Key = "$utos:blob";

    private const string Prefix = "$utos:";

    /// <summary>
    /// Whether this key is one the encoding would otherwise claim: <c>$utos:…</c>, and also
    /// <c>$$utos:…</c> and beyond, which are what escaping produces and so must themselves escape
    /// or the transformation would not be reversible.
    /// </summary>
    public static bool IsAtRisk(string key)
    {
        var dollars = 0;
        while (dollars < key.Length && key[dollars] == '$') dollars++;

        return dollars > 0
               && key.AsSpan(dollars).StartsWith(Prefix.AsSpan(1), StringComparison.Ordinal);
    }

    /// <summary>
    /// A key as it is written. One extra <c>$</c> for an at-risk key and nothing at all for any
    /// other, so the overwhelming majority of values are byte-for-byte what they were before blobs
    /// existed.
    /// </summary>
    public static string Escape(string key) => IsAtRisk(key) ? "$" + key : key;

    /// <summary>The inverse of <see cref="Escape"/>.</summary>
    public static string Unescape(string key) =>
        key.Length > 1 && key[0] == '$' && key[1] == '$' && IsAtRisk(key) ? key[1..] : key;

    /// <summary>
    /// Writes the wrapper and the blob inside it. Absent fields are omitted rather than written as
    /// defaults: these values are copied at every hop and written to orchestration history, so the
    /// common case — a stored blob with no name and no offset — is three fields.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, WorkflowBlob blob)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(Key);
        WriteBody(writer, blob);
        writer.WriteEndObject();
    }

    /// <summary>Just the inner object, for the converter on <see cref="WorkflowBlob"/> itself.</summary>
    public static void WriteBody(Utf8JsonWriter writer, WorkflowBlob blob)
    {
        writer.WriteStartObject();

        if (blob.Id is { } id) writer.WriteString("id", id);
        if (blob.Data is { } data) writer.WriteBase64String("data", data);
        if (blob.Offset != 0) writer.WriteNumber("offset", blob.Offset);

        writer.WriteNumber("size", blob.Size);

        // Written even when it is the default. It is the one field an author can read off a blob
        // whatever its backing, and a missing `type` reading back as the default would be
        // indistinguishable from one a producer declared.
        writer.WriteString("type", blob.MediaType);

        if (blob.Name is { } name) writer.WriteString("name", name);
        if (blob.LastModified is { } modified) writer.WriteString("lastModified", modified);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Reads the inner object, with the reader positioned on its <c>{</c>. Leaves the reader on the
    /// matching <c>}</c>, as a converter must.
    /// </summary>
    public static WorkflowBlob ReadBody(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"A '{Key}' value must be an object.");

        string? id = null;
        byte[]? data = null;
        long offset = 0;
        long size = 0;
        var mediaType = "application/octet-stream";
        string? name = null;
        DateTimeOffset? lastModified = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name");

            var property = reader.GetString()!;
            reader.Read();

            switch (property)
            {
                case "id": id = reader.GetString(); break;
                case "data": data = reader.GetBytesFromBase64(); break;
                case "offset": offset = reader.GetInt64(); break;
                case "size": size = reader.GetInt64(); break;
                case "type": mediaType = reader.GetString()!; break;
                case "name": name = reader.GetString(); break;
                case "lastModified": lastModified = reader.GetDateTimeOffset(); break;

                // An unknown field is skipped rather than refused: this is the daemon's own
                // encoding, so an unknown field means a newer writer, and a run in flight across a
                // deploy should keep moving.
                default: reader.Skip(); break;
            }
        }

        return new WorkflowBlob(id, data, offset, size, mediaType, name, lastModified);
    }
}

/// <summary>
/// The shape a <see cref="WorkflowBlob"/> takes wherever it is serialized, whether as an
/// <c>object</c>-typed leaf of a run value or as a declared field such as a response's body.
/// <para>
/// On the type rather than in each converter, so there is one shape and not two. It writes the
/// inner object alone — the <c>$utos:blob</c> wrapper is what
/// <see cref="ObjectDictionaryJsonConverter"/> adds, because a declared <c>WorkflowBlob</c> field
/// needs no discriminator and an untyped leaf does.
/// </para>
/// </summary>
public sealed class WorkflowBlobJsonConverter : JsonConverter<WorkflowBlob>
{
    public override WorkflowBlob Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WorkflowBlobJson.ReadBody(ref reader);

    public override void Write(
        Utf8JsonWriter writer, WorkflowBlob value, JsonSerializerOptions options) =>
        WorkflowBlobJson.WriteBody(writer, value);
}
