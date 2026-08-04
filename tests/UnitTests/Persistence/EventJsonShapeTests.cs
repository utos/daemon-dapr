using System.Text.Json;
using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Events;

namespace UnitTests.Persistence;

/// <summary>
/// Pins the JSON an event serializes to. <c>EventSerializer</c> uses plain
/// <see cref="JsonSerializer"/> with no options, so the shape rests entirely on the converters the
/// types themselves carry — and a round-trip test would pass whatever the shape became. It is
/// asserted directly, as text, so that a change to it has to be a decision.
///
/// This is one half of the persistence contract; <see cref="EventTypeRegistryTests"/> is the
/// other, pinning the name each event is stored under. A shape change now fails loudly on read
/// rather than silently — a stored event that will not deserialize throws, and the readers no
/// longer skip past it — but "loudly" means a failed load, not a caught mistake. Changing the
/// shape still costs a version bump and an upcaster, which is what these literals are here to
/// make you notice.
///
/// An execution id must stay a bare GUID string, which is what a raw <c>Guid</c> wrote before
/// <see cref="ExecutionId"/> existed. The same JSON also crosses the Dapr activity boundary, so
/// an orchestration in flight during a deploy depends on it too.
/// </summary>
public class EventJsonShapeTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTime At = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Serialized the way <c>EventSerializer.SerializeEvent</c> does it: runtime type, no options.</summary>
    private static string Serialize(object @event) =>
        JsonSerializer.Serialize(@event, @event.GetType());

    [Fact]
    public void An_execution_id_is_a_bare_guid_string()
    {
        Assert.Equal("\"11111111-1111-1111-1111-111111111111\"", JsonSerializer.Serialize(Id));
    }

    [Fact]
    public void Scheduled_keeps_its_shape()
    {
        Assert.Equal(
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}""",
            Serialize(new WorkflowExecutionScheduledEvent(Id, At)));
    }

    [Fact]
    public void Active_keeps_its_shape()
    {
        Assert.Equal(
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}""",
            Serialize(new WorkflowExecutionActiveEvent(Id, At)));
    }

    [Fact]
    public void Completed_keeps_its_shape()
    {
        Assert.Equal(
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}""",
            Serialize(new WorkflowExecutionCompletedEvent(Id, At)));
    }

    [Fact]
    public void Failed_keeps_its_shape()
    {
        Assert.Equal(
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","Reason":"HTTP 500","DateOccurred":"2026-07-31T12:00:00Z"}""",
            Serialize(new WorkflowExecutionFailedEvent(Id, "HTTP 500", At)));
    }

    private static WorkflowBundle EmptyBundle() =>
        new("send-email:1.0.0",
            new Dictionary<string, Workflow>
            {
                ["send-email:1.0.0"] = new(
                    "utos.io/v1",
                    "Workflow",
                    new WorkflowMetadata("send-email", "1.0.0"),
                    WorkflowSpec.Empty)
            });

    [Fact]
    public void Created_keeps_its_shape()
    {
        var bundle = EmptyBundle();

        var created = new WorkflowExecutionCreatedEvent(
            Id, bundle, new Dictionary<string, object>(), "start", At, null, "sha256:abc");

        var json = Serialize(created);

        // The id is what this test exists for; asserted on its own so a failure points at it
        // rather than at a 400-character string diff.
        Assert.StartsWith(
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","Bundle":{""", json);

        Assert.Contains("\"Metadata\":{\"Name\":\"send-email\",\"Version\":\"1.0.0\"", json);
        Assert.EndsWith(
            "\"StartActivity\":\"start\",\"DateOccurred\":\"2026-07-31T12:00:00Z\"," +
            "\"AmbientEnv\":null,\"Digest\":\"sha256:abc\"}",
            json);
    }

    /// <summary>
    /// The run input, which <see cref="Created_keeps_its_shape"/> leaves unpinned because it
    /// asserts an empty dictionary. Every leaf type a client can send appears here, because this
    /// is the segment a change of converter would alter — and altering it would not fail, it
    /// would drop the event on the next read and rehydrate the aggregate with a hole.
    ///
    /// Note "whole":5 — a double whose value is whole writes without a decimal point, which is
    /// why a "5.0 stays a double" guarantee cannot survive a round trip through the event store.
    /// </summary>
    [Fact]
    public void Created_pins_the_shape_of_its_input_values()
    {
        var input = new Dictionary<string, object>
        {
            ["n"] = 1,
            ["big"] = 9007199254740993L,
            ["ratio"] = 2.5,
            ["whole"] = 5.0,
            ["s"] = "x",
            ["b"] = true,
            ["nested"] = new Dictionary<string, object?> { ["k"] = 1 },
            ["list"] = new List<object?> { 1, "two", null }
        };

        var created = new WorkflowExecutionCreatedEvent(
            Id, EmptyBundle(), input, "start", At, null, null);

        Assert.Contains(
            """
            "Input":{"n":1,"big":9007199254740993,"ratio":2.5,"whole":5,"s":"x","b":true,"nested":{"k":1},"list":[1,"two",null]}
            """,
            Serialize(created));
    }

    /// <summary>
    /// The whole point of pinning the shape: an event written before the change still reads
    /// back, so an aggregate rehydrates its full history rather than a truncated one.
    /// </summary>
    [Fact]
    public void An_event_written_with_a_raw_guid_still_deserializes()
    {
        const string legacy =
            """{"ExecutionId":"11111111-1111-1111-1111-111111111111","DateOccurred":"2026-07-31T12:00:00Z"}""";

        var restored = JsonSerializer.Deserialize<WorkflowExecutionScheduledEvent>(legacy);

        Assert.Equal(Id, restored!.ExecutionId);
        Assert.Equal(At, restored.DateOccurred);
    }
}
