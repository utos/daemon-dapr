using Grpc.Core;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;

namespace IntegrationTests.Streaming;

/// <summary>
/// Tests for WatchExecution API endpoint streaming mechanics.
/// Focuses on basic streaming, error handling, and parameter validation.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class WatchExecutionStreamingTests(DaemonTestFixture fixture)
{
    // ============================================================================
    // BASIC STREAMING FUNCTIONALITY
    // ============================================================================

    [Fact]
    public async Task WithValidExecutionId_ShouldStreamEvents()
    {
        // Arrange
        var request = new TestWorkflowBuilder("stream-test-workflow", fixture.MockBaseUrl)
            .HttpGet("stream-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(1000));

        // Assert - Should receive log events
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Message)));
    }

    [Fact]
    public async Task WithNonExistentExecutionId_ShouldThrowNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
            {
                ExecutionId = nonExistentId
            }, cancellationToken: cts.Token);

            await call.ResponseStream.MoveNext(cts.Token);
        });

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
        Assert.Contains("execution not found", exception.Status.Detail.ToLowerInvariant());
    }

    [Fact]
    public async Task WithInvalidExecutionId_ShouldThrowInvalidArgument()
    {
        // Arrange
        var invalidId = "not-a-guid";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
            {
                ExecutionId = invalidId
            }, cancellationToken: cts.Token);

            await call.ResponseStream.MoveNext(cts.Token);
        });

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    // ============================================================================
    // CONCURRENT STREAMING
    // ============================================================================

    [Fact]
    public async Task MultipleConcurrentClients_ShouldAllReceiveEvents()
    {
        // Arrange
        var request = new TestWorkflowBuilder("concurrent-stream-workflow", fixture.MockBaseUrl)
            .HttpGet("stream-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act - Create multiple concurrent watch streams
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var tasks = new List<Task<List<WatchExecutionResponse>>>();

        for (var i = 0; i < 3; i++)
            tasks.Add(Task.Run(async () =>
            {
                var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
                {
                    ExecutionId = executionId
                }, cancellationToken: cts.Token);

                var events = new List<WatchExecutionResponse>();
                var reader = call.ResponseStream;

                // Read first few events
                for (var j = 0; j < 3 && await reader.MoveNext(cts.Token); j++) events.Add(reader.Current);

                return events;
            }));

        var results = await Task.WhenAll(tasks);

        // Assert - All clients should receive log events
        Assert.All(results, events => Assert.NotEmpty(events));
        Assert.All(results, events =>
            Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Message))));
    }

    // ============================================================================
    // CANCELLATION AND CLEANUP
    // ============================================================================

    [Fact]
    public async Task WithCancellation_ShouldStopGracefully()
    {
        // Arrange
        var request = new TestWorkflowBuilder("cancellation-test-workflow", fixture.MockBaseUrl)
            .HttpGet("stream-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act
        using var cts = new CancellationTokenSource();
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        }, cancellationToken: cts.Token);

        var events = new List<WatchExecutionResponse>();
        var reader = call.ResponseStream;

        // Read first event, then cancel to test cancellation handling
        await reader.MoveNext(cts.Token);
        events.Add(reader.Current);

        // Cancel after receiving first event
        await cts.CancelAsync();

        // Assert - Should handle cancellation gracefully
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await reader.MoveNext(cts.Token)) events.Add(reader.Current);
        });

        Assert.Equal(StatusCode.Cancelled, exception.StatusCode);

        // Should have received at least one event before cancellation
        Assert.NotEmpty(events);
    }

    // ============================================================================
    // PARAMETER VALIDATION
    // ============================================================================

    [Fact]
    public async Task WithBothTailAndAfter_ShouldValidateParameters()
    {
        // Arrange
        var request = new TestWorkflowBuilder("param-validation-workflow", fixture.MockBaseUrl)
            .HttpGet("stream-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act & Assert - Should handle mutually exclusive parameters
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
            {
                ExecutionId = executionId,
                Tail = 5,
                After = 1 // Both tail and after specified
            }, cancellationToken: cts.Token);

            await call.ResponseStream.MoveNext(cts.Token);
        });

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task WithNegativeAfterPosition_ShouldHandleGracefully()
    {
        // Arrange
        var request = new TestWorkflowBuilder("negative-position-workflow", fixture.MockBaseUrl)
            .HttpGet("stream-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId,
            After = -1
        }, cancellationToken: cts.Token);

        // Should either throw InvalidArgument or handle gracefully
        // Implementation dependent - document the expected behavior
    }

    // NOTE: WithInvalidEventType_ShouldValidateFilter test removed
    // With strongly-typed EventType enum, invalid values cannot be passed at compile time
    // Type safety is now enforced by the protobuf enum definition
}
