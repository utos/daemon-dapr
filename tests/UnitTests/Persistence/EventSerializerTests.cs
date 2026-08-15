using System.Text;
using System.Text.Json.Nodes;
using Common.Infrastructure.EventStore;
using Common.SeedWork;
using Execution.Domain.Events;

namespace UnitTests.Persistence;

/// <summary>
/// <c>EventSerializer</c> itself, which nothing exercised before: <see cref="EventJsonShapeTests"/>
/// re-implements its serialize call rather than invoking it, so neither the stored name, the
/// version tag, nor the failure path had a test.
///
/// The failure path is most of what is asserted here. Deserialization used to catch every
/// exception and return null, and both readers took null as "skip" — an aggregate rehydrated with
/// a hole at the correct revision, and a projection was acked without running. Every "throws"
/// below is a case that used to disappear into a log line.
/// </summary>
public class EventSerializerTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTime At = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    private static EventSerializer Serializer(params IEventUpcaster[] upcasters) =>
        new(new EventTypeRegistry(typeof(WorkflowExecutionCreatedEvent).Assembly),
            new EventUpcasterRegistry(upcasters));

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void An_event_is_stored_under_its_declared_name_at_its_declared_version()
    {
        var serialized = Serializer().SerializeEvent(new WorkflowExecutionScheduledEvent(Id, At));

        Assert.Equal("utos.execution.scheduled", serialized.EventType);
        Assert.Equal("""{"v":1}""", Encoding.UTF8.GetString(serialized.Metadata));
        Assert.Equal(
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}""",
            Encoding.UTF8.GetString(serialized.Data));
    }

    [Fact]
    public void An_event_round_trips()
    {
        var serializer = Serializer();
        var original = new WorkflowExecutionFailedEvent(Id, "HTTP 500", At);

        Assert.Equal(original, serializer.DeserializeEvent(serializer.SerializeEvent(original)));
    }

    /// <summary>
    /// The whole point of the registry: the stored name resolves without reference to where the
    /// class lives, so moving or renaming it does not make the stream unreadable.
    /// </summary>
    [Fact]
    public void A_name_resolves_without_any_assembly_qualification()
    {
        var restored = Serializer().DeserializeEvent(
            "utos.execution.active",
            Utf8("""{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}"""),
            Utf8("""{"v":1}"""));

        Assert.Equal(new WorkflowExecutionActiveEvent(Id, At), restored);
    }

    [Fact]
    public void An_unknown_name_throws_rather_than_returning_null()
    {
        var exception = Assert.Throws<EventDeserializationException>(() => Serializer().DeserializeEvent(
            "Execution.Domain.Events.WorkflowExecutionActiveEvent, Execution.Domain",
            Utf8("{}"),
            Utf8("""{"v":1}""")));

        Assert.Contains("Execution.Domain.Events.WorkflowExecutionActiveEvent", exception.Message);
    }

    [Fact]
    public void Malformed_json_throws_rather_than_returning_null()
    {
        Assert.Throws<EventDeserializationException>(() => Serializer().DeserializeEvent(
            "utos.execution.active", Utf8("{ not json"), Utf8("""{"v":1}""")));
    }

    /// <summary>A literal null body deserializes to null and casting null succeeds, so it needs its own guard.</summary>
    [Fact]
    public void A_body_that_deserializes_to_nothing_throws()
    {
        Assert.Throws<EventDeserializationException>(() => Serializer().DeserializeEvent(
            "utos.execution.active", Utf8("null"), Utf8("""{"v":1}""")));
    }

    [Fact]
    public void A_version_newer_than_this_daemon_knows_throws()
    {
        var exception = Assert.Throws<EventDeserializationException>(() => Serializer().DeserializeEvent(
            "utos.execution.active",
            Utf8("""{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}"""),
            Utf8("""{"v":9}""")));

        Assert.Contains("newer daemon", exception.Message);
    }

    /// <summary>
    /// Absent, empty and unreadable metadata all read as v1 — the version is a claim about the
    /// body, and an untagged body is exactly what v1 means.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Metadata_without_a_usable_version_reads_as_version_one(string metadata)
    {
        var restored = Serializer().DeserializeEvent(
            "utos.execution.active",
            Utf8("""{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}"""),
            Utf8(metadata));

        Assert.Equal(new WorkflowExecutionActiveEvent(Id, At), restored);
    }

    /// <summary>
    /// The seam, proven end to end on a synthetic event rather than a real one: nothing in the
    /// daemon needs an upcaster yet, and the point of building the pipeline before it does is
    /// that the first real schema change is a change of data, not of design.
    /// </summary>
    [Fact]
    public void A_payload_at_an_older_version_is_upcast_before_it_is_deserialized()
    {
        var serializer = new EventSerializer(
            EventTypeRegistry.For(typeof(RenamedFieldEvent)),
            new EventUpcasterRegistry([new RenamedFieldUpcaster()]));

        var restored = serializer.DeserializeEvent(
            "utos.test.renamed-field",
            Utf8("""{"OldName":"kept","DateOccurred":"2026-07-31T12:00:00Z"}"""),
            Utf8("""{"v":1}"""));

        Assert.Equal(new RenamedFieldEvent("kept", At), restored);
    }

    [Fact]
    public void A_missing_rung_in_the_upcast_chain_throws()
    {
        var serializer = new EventSerializer(
            EventTypeRegistry.For(typeof(RenamedFieldEvent)),
            new EventUpcasterRegistry([]));

        var exception = Assert.Throws<EventDeserializationException>(() => serializer.DeserializeEvent(
            "utos.test.renamed-field",
            Utf8("""{"OldName":"kept","DateOccurred":"2026-07-31T12:00:00Z"}"""),
            Utf8("""{"v":1}""")));

        Assert.Contains("v1 -> v2", exception.Message);
    }

    [Fact]
    public void Two_upcasters_claiming_the_same_step_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => new EventUpcasterRegistry([new RenamedFieldUpcaster(), new RenamedFieldUpcaster()]));
    }

    /// <summary>An event whose v2 renamed a field — the cheapest change a nullable field cannot absorb.</summary>
    [EventType("utos.test.renamed-field", Version = 2)]
    private record RenamedFieldEvent(string NewName, DateTime DateOccurred) : IDomainEvent;

    private sealed class RenamedFieldUpcaster : IEventUpcaster
    {
        public string EventTypeName => "utos.test.renamed-field";

        public int FromVersion => 1;

        public JsonObject Upcast(JsonObject payload)
        {
            payload["NewName"] = payload["OldName"]?.DeepClone();
            payload.Remove("OldName");
            return payload;
        }
    }
}
