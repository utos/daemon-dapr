using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for workflow execution behavior - verifying that workflows actually execute activities correctly.
/// This includes verifying HTTP calls are made, data is passed correctly, etc.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class WorkflowExecutionBehaviorTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task SimpleWorkflow_ShouldCallHttpActivity()
    {
        // Arrange
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/test-endpoint").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"success\"}"));

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/test-endpoint")
            .Build();

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(300));

        // Assert - Verify WireMock received the HTTP call
        var logEntries = fixture.WireMockServer.LogEntries;
        var testEndpointCalls = logEntries.Where(e => e.RequestMessage.Path == "/test-endpoint").ToList();

        Assert.Single(testEndpointCalls);
        Assert.Equal("GET", testEndpointCalls[0].RequestMessage.Method);
    }

    [Fact]
    public async Task MultipleActivities_ShouldCallAllInOrder()
    {
        // Arrange
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/first").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"step\": \"first\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/second").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"step\": \"second\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/third").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"step\": \"third\"}"));

        var request = new TestWorkflowBuilder("multi-activity-workflow", fixture.MockBaseUrl)
            .HttpGet("first-activity", "/first")
            .HttpGet("second-activity", "/second")
            .HttpGet("third-activity", "/third")
            .BuildSequential();

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Verify all three HTTP calls were made in order
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var firstCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/first");
        var secondCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/second");
        var thirdCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/third");

        Assert.NotNull(firstCall);
        Assert.NotNull(secondCall);
        Assert.NotNull(thirdCall);

        // Verify order
        var firstIndex = logEntries.IndexOf(firstCall);
        var secondIndex = logEntries.IndexOf(secondCall);
        var thirdIndex = logEntries.IndexOf(thirdCall);

        Assert.True(firstIndex < secondIndex, "First activity should be called before second");
        Assert.True(secondIndex < thirdIndex, "Second activity should be called before third");
    }

    // TODO: Add test for POST with body data
    // TODO: Add test for headers being passed correctly
    // TODO: Add test for templating in URLs
    // TODO: Add test for activity output being available to subsequent activities
}
