using System.Text.Json;
using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Events;

namespace UnitTests.Persistence;

/// <summary>
/// Pins the JSON an event serializes to. <c>EventSerializer</c> uses plain
/// <see cref="JsonSerializer"/> with no options, and — critically — its deserialize path catches
/// every exception, logs it and returns null, after which <c>EventStoreRepository</c> drops that
/// event while still counting its stream revision. A shape change therefore does not fail: an
/// aggregate silently rehydrates with a hole in its history. So the shape is asserted directly,
/// as text, rather than through a round-trip that would pass either way.
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

    [Fact]
    public void Created_keeps_its_shape()
    {
        var bundle = new WorkflowBundle(
            "send-email:1.0.0",
            new Dictionary<string, Workflow>
            {
                ["send-email:1.0.0"] = new(
                    "utos.io/v1",
                    "Workflow",
                    new WorkflowMetadata("send-email", "1.0.0"),
                    WorkflowSpec.Empty)
            });

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
