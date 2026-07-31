using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

[Collection(DaemonTestCollection.Name)]
public class ExecutionFailureTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task WorkflowWithFailingActivity_ShouldTransitionToFailed()
    {
        // Arrange - Create workflow with activity that returns HTTP 500
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/fail").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Internal server error\"}"));

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/fail")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Watch execution
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Stream should close after workflow fails (store.Complete() called on terminal event)
        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(300));

        // Should have received log events including a failure log
        var failureLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Workflow execution failed"));
        Assert.NotNull(failureLog);

        // Should have received a structured terminal frame carrying the failed status and error
        var terminal = events.FirstOrDefault(e => e.Status == ExecutionStatus.Failed);
        Assert.NotNull(terminal);
        Assert.NotNull(terminal.Error);
        Assert.Equal("EXECUTION_FAILED", terminal.Error.Code);
        Assert.False(string.IsNullOrEmpty(terminal.Error.Message));

        // Verify the failing activity was actually called
        var logEntries = fixture.WireMockServer.LogEntries;
        var failedCalls = logEntries.Where(e => e.RequestMessage.Path == "/fail").ToList();
        Assert.Single(failedCalls);
    }

    [Fact]
    public async Task FailedWorkflow_ShouldStopExecution()
    {
        // Arrange - Create workflow with 5 activities, where the 3rd one fails
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/activity-1").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"success-1\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/activity-2").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"success-2\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/activity-3-fail").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Internal server error\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/activity-4").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"success-4\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/activity-5").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"success-5\"}"));

        var request = new TestWorkflowBuilder("multi-activity-workflow", fixture.MockBaseUrl)
            .HttpGet("activity-1", "/activity-1")
            .HttpGet("activity-2", "/activity-2")
            .HttpGet("activity-3-fail", "/activity-3-fail")
            .HttpGet("activity-4", "/activity-4")
            .HttpGet("activity-5", "/activity-5")
            .BuildSequential();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Watch execution
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Stream should close after workflow fails
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Verify that activities 1 and 2 were called (before the failure)
        var logEntries = fixture.WireMockServer.LogEntries;
        var activity1Calls = logEntries.Where(e => e.RequestMessage.Path == "/activity-1").ToList();
        var activity2Calls = logEntries.Where(e => e.RequestMessage.Path == "/activity-2").ToList();
        var activity3Calls = logEntries.Where(e => e.RequestMessage.Path == "/activity-3-fail").ToList();
        var activity4Calls = logEntries.Where(e => e.RequestMessage.Path == "/activity-4").ToList();
        var activity5Calls = logEntries.Where(e => e.RequestMessage.Path == "/activity-5").ToList();

        Assert.Single(activity1Calls);
        Assert.Single(activity2Calls);
        Assert.Single(activity3Calls);

        // Activities 4 and 5 should NOT have been called (after the failure)
        Assert.Empty(activity4Calls);
        Assert.Empty(activity5Calls);
    }
}
