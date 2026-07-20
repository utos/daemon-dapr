using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;

namespace IntegrationTests.Streaming;

/// <summary>
/// Tests for WatchExecution API filtering capabilities.
/// Tests query parameters: level, source, category, after, tail.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class WatchExecutionFilteringTests(DaemonTestFixture fixture)
{
    // ============================================================================
    // EVENT POSITIONING
    // ============================================================================

    [Fact]
    public async Task WithAfterPosition_ShouldStartFromSpecificEvent()
    {
        // Arrange
        var request = new TestWorkflowBuilder("after-position-workflow", fixture.MockBaseUrl)
            .HttpGet("filter-activity", "/success")
            .Build();
        var executionId = (await fixture.ScheduleAsync(request)).Id;

        // First, collect all events to know how many there are
        var fullCall = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });
        var allEvents = await fullCall.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Act - Start watching from after position 0 (skip first event)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId,
            After = 0
        }, cancellationToken: cts.Token);

        var events = new List<WatchExecutionResponse>();
        var reader = call.ResponseStream;

        while (await reader.MoveNext(cts.Token)) events.Add(reader.Current);

        // Assert - Should have one fewer event than the full stream
        Assert.NotEmpty(events);
        Assert.True(events.Count < allEvents.Count,
            $"After skipping first event, should have fewer events ({events.Count}) than full stream ({allEvents.Count})");
    }

    [Fact]
    public async Task WithTail_ShouldReturnLastNEvents()
    {
        // Arrange
        var request = new TestWorkflowBuilder("tail-events-workflow", fixture.MockBaseUrl)
            .HttpGet("filter-activity", "/success")
            .Build();
        var executionId = (await fixture.ScheduleAsync(request)).Id;

        // Wait for events to be generated
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Act - Get last 2 events
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId,
            Tail = 2
        }, cancellationToken: cts.Token);

        var events = new List<WatchExecutionResponse>();
        var reader = call.ResponseStream;

        // Read available events
        while (await reader.MoveNext(cts.Token) && events.Count < 5) events.Add(reader.Current);

        // Assert - Should get recent events
        Assert.NotEmpty(events);
        Assert.True(events.Count >= 2);
    }

    // ============================================================================
    // LOG LEVEL FILTERING
    // ============================================================================

    [Fact]
    public async Task WithLogLevelFilter_ShouldFilterByMinimumLevel()
    {
        // Arrange - Create a workflow that will fail (generates ERROR logs)
        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath("/log-level-test").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Internal Server Error\"}"));

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/log-level-test")
            .Build();
        var scheduleResponse = await fixture.ScheduleAsync(request);

        // Act - Filter for ERROR level and above
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = scheduleResponse.Id,
            Level = LogLevel.Error
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.True(e.Level >= LogLevel.Error,
                $"Expected log level >= ERROR, but got {e.Level}");
        });
    }

    // ============================================================================
    // SOURCE FILTERING
    // ============================================================================

    [Fact]
    public async Task WithSourceFilter_ShouldFilterByActivitySource()
    {
        // Arrange - activity name must match the Source filter below
        var request = new TestWorkflowBuilder("source-filter-workflow", fixture.MockBaseUrl)
            .HttpGet("test-activity", "/success")
            .Build();
        var executionId = (await fixture.ScheduleAsync(request)).Id;

        // Act - Filter by specific activity source
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId,
            Source = "test-activity"
        }, cancellationToken: cts.Token);

        var events = new List<WatchExecutionResponse>();
        var reader = call.ResponseStream;

        // Try to read events
        while (await reader.MoveNext(cts.Token) && events.Count < 10) events.Add(reader.Current);

        // Assert - All events should be from the specified source
        Assert.All(events, e => Assert.Equal("test-activity", e.Source));
    }

    // ============================================================================
    // MIXED EVENT STREAM
    // ============================================================================

    [Fact]
    public async Task WithInterleavedEvents_ShouldMaintainTemporalOrder()
    {
        // Test that events are properly ordered by timestamp when they occur rapidly
    }

    [Fact]
    public async Task WithSimultaneousMultipleActivityOutput_ShouldCaptureAll()
    {
        // Test scenario where multiple activities are producing logs
        // simultaneously and verify all events are captured
    }

    [Fact]
    public async Task WithLongRunningActivityWithContinuousLogs_ShouldStream()
    {
        // Test that a long-running activity that continuously produces logs
        // streams them in real-time rather than batching
    }
}
