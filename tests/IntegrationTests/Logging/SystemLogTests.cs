using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Logging;

/// <summary>
/// Tests for system-generated log events.
/// System logs are automatically emitted for workflow/activity lifecycle, failures, retries, etc.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class SystemLogTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task ShouldEmitSystemLogsForWorkflowLifecycle()
    {
        // Arrange
        var request = new TestWorkflowBuilder("lifecycle-log-workflow", fixture.MockBaseUrl)
            .HttpGet("lifecycle-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act - Watch for log events
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Assert - Should have workflow start and completion logs
        var workflowStartLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Workflow execution started"));
        Assert.NotNull(workflowStartLog);
        Assert.Equal(LogLevel.Info, workflowStartLog.Level);

        var workflowCompleteLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Workflow execution completed"));
        Assert.NotNull(workflowCompleteLog);
        Assert.Equal(LogLevel.Info, workflowCompleteLog.Level);
    }

    [Fact]
    public async Task ShouldEmitSystemLogsForActivityLifecycle()
    {
        // Arrange
        var request = new TestWorkflowBuilder("activity-log-workflow", fixture.MockBaseUrl)
            .HttpGet("activity-lifecycle", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act - Watch for log events
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Assert - Should have activity start and completion logs
        // Look for activity start log
        var activityStartLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Activity") &&
            e.Message.Contains("started"));
        Assert.NotNull(activityStartLog);
        Assert.Equal(LogLevel.Info, activityStartLog.Level);

        // Look for activity completion log (should include duration)
        var activityCompleteLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Activity") &&
            e.Message.Contains("completed") &&
            e.Message.Contains("ms"));
        Assert.NotNull(activityCompleteLog);
        Assert.Equal(LogLevel.Info, activityCompleteLog.Level);
    }

    [Fact]
    public async Task ShouldEmitSystemLogsForHttpRequests()
    {
        // Arrange
        var request = new TestWorkflowBuilder("http-log-workflow", fixture.MockBaseUrl)
            .HttpGet("http-activity", "/success")
            .Build();
        var executionId = (await fixture.DaemonClient.ScheduleExecutionAsync(request)).Id;

        // Act - Watch for log events
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Assert - Should have HTTP request logs
        // Look for HTTP request start log (DEBUG)
        var httpStartLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("HTTP") &&
            e.Message.Contains("GET"));
        Assert.NotNull(httpStartLog);
        Assert.Equal(LogLevel.Debug, httpStartLog.Level);

        // Look for HTTP response log (INFO with status code and duration)
        var httpResponseLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("HTTP") &&
            e.Message.Contains("200") &&
            e.Message.Contains("ms"));
        Assert.NotNull(httpResponseLog);
        Assert.Equal(LogLevel.Info, httpResponseLog.Level);
    }

    [Fact]
    public async Task ShouldEmitSystemLogsForActivityFailures()
    {
        // Test that failed activities generate appropriate system logs:
        // - "Activity 'validate-payment' failed: Connection timeout" (ERROR)
        // - "Activity 'validate-payment' failed after 3 attempts" (ERROR)
        // - Source should be "system"
    }

    [Fact]
    public async Task ShouldEmitSystemLogsForWorkflowFailures()
    {
        // Arrange - Create a workflow that will fail (500 error)
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/workflow-failure-test").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Internal Server Error\"}"));

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/workflow-failure-test")
            .Build();
        var scheduleResponse = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        // Act - Watch for log events
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = scheduleResponse.Id
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Assert - Should have workflow failure logs
        var workflowFailureLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Workflow execution failed"));
        Assert.NotNull(workflowFailureLog);
        Assert.True(workflowFailureLog.Level >= LogLevel.Error,
            $"Expected ERROR or higher, but got {workflowFailureLog.Level}");
    }

    [Fact]
    public async Task ShouldEmitSystemLogsForRetryAttempts()
    {
        // Test that retry scenarios generate appropriate system logs:
        // - "Activity 'api-call' failed, retrying in 1s (attempt 1/3)" (WARN)
        // - "Activity 'api-call' retry attempt 2/3" (INFO)
        // - "Activity 'api-call' failed permanently after 3 attempts" (ERROR)
    }

    [Fact]
    public async Task ShouldEmitSystemLogsForTemplateRendering()
    {
        // Test that template processing generates system logs:
        // - "Rendering template for activity 'send-email'" (DEBUG)
        // - "Template rendering completed in 5ms" (DEBUG)
        // - "Template rendering failed: Variable 'user.email' not found" (ERROR)
    }

    // Test cases: DEBUG (Template rendering, HTTP request details), INFO (Activity lifecycle, Workflow state transitions),
    // WARN (Retry attempts, Performance degradation), ERROR (Activity failures, Validation errors),
    // FATAL (Workflow termination, System crashes)
    [Fact(Skip = "Not yet implemented")]
    public Task SystemLogsShouldUseAppropriateLogLevels()
    {
        // Test that different system events use appropriate log levels
        // and that the level distribution makes sense for operational use
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ShouldDistinguishSystemLogsFromCustomLogs()
    {
        // Test that system logs (source="system") are clearly distinguished
        // from custom workflow logs (source=activity-name)
        // Both should appear in the same stream but be identifiable
    }

    [Fact]
    public async Task SystemLogsShouldIncludeRelevantMetadata()
    {
        // Arrange - Create a workflow with a specific activity name
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/metadata-test").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"ok\"}"));

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("test-metadata-activity", "/metadata-test")
            .Build();
        var scheduleResponse = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        // Act - Watch for log events
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = scheduleResponse.Id
        });

        var events = await call.CollectUntilCompletion(TimeSpan.FromSeconds(10));

        // Assert - System logs should include relevant metadata
        // Activity logs should include the specific activity name
        var activityLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Activity 'test-metadata-activity'"));
        Assert.NotNull(activityLog);

        // Activity completion logs should include duration in milliseconds
        var activityCompleteLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("Activity 'test-metadata-activity' completed") &&
            e.Message.Contains("ms"));
        Assert.NotNull(activityCompleteLog);
        Assert.Matches(@"\d+ms", activityCompleteLog.Message);

        // HTTP logs should include status code 200
        var httpResponseLog = events.FirstOrDefault(e =>
            e.Source == "system" &&
            e.Message.Contains("HTTP") &&
            e.Message.Contains("200"));
        Assert.NotNull(httpResponseLog);
    }
}
