using System.Text.Json;
using Common.SeedWork;
using Execution.Domain.Interpreter;
using Execution.Infrastructure.Dapr;

namespace UnitTests.Dapr;

/// <summary>
/// <see cref="WorkflowExecutorInput"/> is the continue-as-new envelope, and a <c>with</c>
/// expression carries anything it does not name straight through. That makes the dangerous
/// failure a quiet one: state a segment <em>changed</em> and did not write back does not reset to
/// a default, it <em>rewinds</em> to whatever it held when the segment began — every
/// <c>ContinueAsNewAfterTransitions</c> transitions, with nothing failing. A rewound subscription
/// cursor re-delivers values the caller already handled.
///
/// So these assert the two halves separately: live state must come from the segment, and
/// everything else must survive untouched. The reflection guard at the end is what catches a
/// property added later and never classified as either.
/// </summary>
public class ContinuationEnvelopeTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ExecutionId ParentId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ExecutionId ChildId = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTime Started = new(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>An envelope with every property set to something distinctive.</summary>
    private static WorkflowExecutorInput Original() =>
        new(Id,
            "acme/watcher:1.0.0",
            "first",
            ParentId,
            new Dictionary<string, object> { ["stale"] = 1 },
            ["first"],
            LoopDetected: false,
            StartedAt: Started,
            ContinueAsNewAfterTransitions: 7,
            Subscriptions: new Dictionary<string, SubscriptionState>
            {
                ["watch"] = new(ChildId, 3)
            });

    private static ExecutionCursor Cursor() =>
        ExecutionCursor.Start(["first", "handle"], loopDetected: true).Enter("handle").Cursor;

    private static WorkflowExecutorInput Continued(
        Dictionary<string, SubscriptionState>? subscriptions = null) =>
        Original().ContinueFrom(
            "handle",
            new Dictionary<string, object> { ["fresh"] = 2 },
            Cursor(),
            Started,
            subscriptions ?? new Dictionary<string, SubscriptionState>
            {
                ["watch"] = new(ChildId, 9)
            });

    [Fact]
    public void The_next_segment_resumes_at_the_activity_the_last_one_stopped_on()
    {
        var continued = Continued();

        Assert.Equal("handle", continued.StartActivity);
        Assert.Equal(2, continued.ActivityInput!["fresh"]);
        Assert.DoesNotContain("stale", continued.ActivityInput);
    }

    [Fact]
    public void Loop_state_comes_from_the_segments_cursor_not_the_envelope_it_started_with()
    {
        var continued = Continued();

        // The original said false and knew only "first". Carrying either through would restart
        // loop detection and lose the back-edge that triggered this continuation.
        Assert.True(continued.LoopDetected);
        Assert.Contains("handle", continued.Visited!);
        Assert.Contains("first", continued.Visited!);
    }

    [Fact]
    public void A_subscription_cursor_advanced_during_the_segment_is_what_carries_forward()
    {
        // The exact rewind this whole shape exists to prevent: the original envelope says 3, the
        // segment consumed up to 9. Carry the record's own value and the caller silently
        // re-handles entries 4 through 9 after every continuation.
        var continued = Continued();

        Assert.Equal(9, continued.Subscriptions!["watch"].Cursor);
        Assert.Equal(ChildId, continued.Subscriptions["watch"].ChildExecutionId);
    }

    [Fact]
    public void A_subscription_that_ended_during_the_segment_does_not_come_back()
    {
        var continued = Continued(new Dictionary<string, SubscriptionState>());

        Assert.Empty(continued.Subscriptions!);
    }

    [Fact]
    public void Identity_and_limits_survive_the_continuation_untouched()
    {
        var continued = Continued();

        Assert.Equal(Id, continued.ExecutionId);
        Assert.Equal("acme/watcher:1.0.0", continued.WorkflowKey);
        Assert.Equal(ParentId, continued.ParentExecutionId);
        Assert.Equal(7, continued.ContinueAsNewAfterTransitions);
        Assert.Equal(Started, continued.StartedAt);
    }

    /// <summary>
    /// The guard that outlives this change. Every property is either supplied by the segment or
    /// deliberately carried; a new one belongs to exactly one of those sets, and the author has to
    /// say which. Left unclassified, it silently takes the "carried" path — which is correct for a
    /// constant and a rewind bug for anything that moves.
    /// </summary>
    [Fact]
    public void Every_property_is_classified_as_either_segment_state_or_carried()
    {
        string[] suppliedBySegment =
        [
            nameof(WorkflowExecutorInput.StartActivity),
            nameof(WorkflowExecutorInput.ActivityInput),
            nameof(WorkflowExecutorInput.Visited),
            nameof(WorkflowExecutorInput.LoopDetected),
            nameof(WorkflowExecutorInput.StartedAt),
            nameof(WorkflowExecutorInput.Subscriptions)
        ];

        string[] carriedUnchanged =
        [
            nameof(WorkflowExecutorInput.ExecutionId),
            nameof(WorkflowExecutorInput.WorkflowKey),
            nameof(WorkflowExecutorInput.ParentExecutionId),
            nameof(WorkflowExecutorInput.ContinueAsNewAfterTransitions)
        ];

        var declared = typeof(WorkflowExecutorInput)
            .GetProperties()
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .Order()
            .ToArray();

        Assert.Equal(suppliedBySegment.Concat(carriedUnchanged).Order().ToArray(), declared);
    }

    /// <summary>
    /// The envelope crosses the Dapr boundary as JSON, where a shape that does not round-trip
    /// fails the way everything else on this boundary does — quietly, with a null where state
    /// used to be.
    /// </summary>
    [Fact]
    public void The_envelope_round_trips_through_the_Dapr_boundary()
    {
        var json = JsonSerializer.Serialize(Continued());

        // An execution id stays a bare GUID string inside the subscription map, as everywhere else.
        Assert.Contains("\"ChildExecutionId\":\"33333333-3333-3333-3333-333333333333\"", json);

        var restored = JsonSerializer.Deserialize<WorkflowExecutorInput>(json)!;

        Assert.Equal(ChildId, restored.Subscriptions!["watch"].ChildExecutionId);
        Assert.Equal(9, restored.Subscriptions["watch"].Cursor);
        Assert.Equal(2, restored.ActivityInput!["fresh"]);
        Assert.True(restored.LoopDetected);
    }
}
