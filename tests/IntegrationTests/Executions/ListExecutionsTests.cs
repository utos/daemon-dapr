using Grpc.Core;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;

namespace IntegrationTests.Executions;

[Collection(DaemonTestCollection.Name)]
public class ListExecutionsTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task ListExecutions_IncludesScheduledExecution()
    {
        // Arrange
        var request = new TestWorkflowBuilder("list-basic-test", fixture.MockBaseUrl,
                "1.0.0")
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        var execution = await WaitForExecutionInList(scheduleResponse.Id, TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(execution);
        Assert.Equal(scheduleResponse.Id, execution.Id);
        Assert.Equal("list-basic-test", execution.Workflow.Name);
        Assert.Equal("1.0.0", execution.Workflow.Version);
    }

    [Fact]
    public async Task ListExecutions_IncludesExecutionStatus()
    {
        // Arrange
        var request = new TestWorkflowBuilder("list-status-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        var execution = await WaitForExecutionInList(scheduleResponse.Id, TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(execution);
        Assert.Contains(execution.Status, new[]
        {
            ExecutionStatus.Unspecified,
            ExecutionStatus.Scheduled,
            ExecutionStatus.Active,
            ExecutionStatus.Completed,
            ExecutionStatus.Failed
        });
    }

    [Fact]
    public async Task ListExecutions_IncludesCreatedTimestamp()
    {
        // Arrange
        var request = new TestWorkflowBuilder("list-timestamp-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        var execution = await WaitForExecutionInList(scheduleResponse.Id, TimeSpan.FromSeconds(5));

        // Assert
        Assert.NotNull(execution);
        Assert.NotNull(execution.CreatedAt);
        var createdAt = execution.CreatedAt.ToDateTime();
        Assert.True(Math.Abs((DateTime.UtcNow - createdAt).TotalSeconds) < 10,
            $"CreatedAt should be within 10 seconds of now. Expected: {DateTime.UtcNow}, Actual: {createdAt}");
    }

    [Fact]
    public async Task ListExecutions_OrdersByCreatedDate_NewestFirst()
    {
        // Arrange - Create two executions with unique names
        var testId = Guid.NewGuid().ToString("N")[..8];

        var request1 = new TestWorkflowBuilder($"order-test-{testId}-1", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();
        var execution1 = await fixture.ScheduleAsync(request1);

        await Task.Delay(200); // Ensure different timestamps

        var request2 = new TestWorkflowBuilder($"order-test-{testId}-2", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();
        var execution2 = await fixture.ScheduleAsync(request2);

        // Wait for both projections
        await WaitForExecutionInList(execution1.Id, TimeSpan.FromSeconds(5));
        await WaitForExecutionInList(execution2.Id, TimeSpan.FromSeconds(5));

        // Act
        var response = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());

        // Assert - Find our test executions
        var ourExecutions = response.Executions
            .Where(e => e.Workflow.Name.StartsWith($"order-test-{testId}"))
            .ToList();

        Assert.Equal(2, ourExecutions.Count);

        // Verify newest first (execution2 should come before execution1)
        var execution2Index = ourExecutions.FindIndex(e => e.Id == execution2.Id);
        var execution1Index = ourExecutions.FindIndex(e => e.Id == execution1.Id);

        Assert.True(execution2Index < execution1Index,
            $"Newer execution should appear before older. execution2 index: {execution2Index}, execution1 index: {execution1Index}");
    }

    [Fact]
    public async Task ListExecutions_IncludesScheduledAt_WhenExecutionIsScheduled()
    {
        // Arrange
        var request = new TestWorkflowBuilder("list-scheduled-at-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);

        // Wait for execution to reach at least Scheduled status
        var execution = await WaitForExecutionWithMinimumStatus(
            scheduleResponse.Id,
            ExecutionStatus.Scheduled,
            TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(execution);
        Assert.NotNull(execution.ScheduledAt);
    }

    [Fact]
    public async Task ListExecutions_IncludesStartedAt_WhenExecutionIsActive()
    {
        // Arrange
        var request = new TestWorkflowBuilder("list-started-at-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);

        // Wait for execution to become active
        var execution = await WaitForExecutionWithMinimumStatus(
            scheduleResponse.Id,
            ExecutionStatus.Active,
            TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(execution);
        Assert.NotNull(execution.StartedAt);
    }

    [Fact]
    public async Task ListExecutions_IncludesFinishedAt_WhenExecutionCompletes()
    {
        // Arrange
        var request = new TestWorkflowBuilder("list-finished-at-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);

        // Wait for execution to complete
        var execution = await WaitForExecutionWithStatus(
            scheduleResponse.Id,
            ExecutionStatus.Completed,
            TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(execution);
        Assert.NotNull(execution.FinishedAt);
    }

    [Fact]
    public async Task ListExecutions_IncludesErrorMessage_WhenExecutionFails()
    {
        // Arrange - Create workflow that will fail
        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath("/list-fail-test").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Internal Server Error\"}"));

        var request = new TestWorkflowBuilder("list-error-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/list-fail-test")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);

        // Wait for execution to fail
        var execution = await WaitForExecutionWithStatus(
            scheduleResponse.Id,
            ExecutionStatus.Failed,
            TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(execution);
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.NotNull(execution.Error);
        Assert.NotEmpty(execution.Error.Message);
        Assert.NotNull(execution.FinishedAt);
    }

    [Fact]
    public async Task ListExecutions_TruncatesLongErrorMessages()
    {
        // Arrange - Create workflow that will fail with a potentially long error
        var longErrorPath = "/list-long-error-test";
        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(longErrorPath).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"" + new string('x', 500) + "\"}")); // Long error body

        var request = new TestWorkflowBuilder("list-truncate-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", longErrorPath)
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        var execution = await WaitForExecutionWithStatus(
            scheduleResponse.Id,
            ExecutionStatus.Failed,
            TimeSpan.FromSeconds(10));

        // Assert
        Assert.NotNull(execution);
        if (execution.Error is { } error)
            Assert.True(error.Message.Length <= 200,
                $"Error message should be truncated to 200 chars. Actual length: {error.Message.Length}");
    }

    [Fact]
    public async Task ListExecutions_FiltersByWorkflowName()
    {
        // Arrange - two distinct workflows
        var testId = Guid.NewGuid().ToString("N")[..8];
        var wantedName = $"filter-name-{testId}-wanted";
        var otherName = $"filter-name-{testId}-other";

        var wanted = await fixture.ScheduleAsync(new TestWorkflowBuilder(wantedName, fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success").Build());
        var other = await fixture.ScheduleAsync(new TestWorkflowBuilder(otherName, fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success").Build());

        await WaitForExecutionInList(wanted.Id, TimeSpan.FromSeconds(5));
        await WaitForExecutionInList(other.Id, TimeSpan.FromSeconds(5));

        // Act - filter to the wanted workflow
        var response = await fixture.DaemonClient.ListExecutionsAsync(
            new ListExecutionsRequest { Workflow = new WorkflowReference { Name = wantedName } });

        // Assert - every returned row is the wanted workflow; the other is excluded
        Assert.All(response.Executions, e => Assert.Equal(wantedName, e.Workflow.Name));
        Assert.Contains(response.Executions, e => e.Id == wanted.Id);
        Assert.DoesNotContain(response.Executions, e => e.Id == other.Id);
    }

    [Fact]
    public async Task ListExecutions_FiltersByVersion_AndOmittedVersionMatchesAll()
    {
        // Arrange - same name, two versions
        var testId = Guid.NewGuid().ToString("N")[..8];
        var name = $"filter-version-{testId}";

        var v1 = await fixture.ScheduleAsync(new TestWorkflowBuilder(name, fixture.MockBaseUrl, "1.0.0")
            .HttpGet("valid-activity", "/success").Build());
        var v2 = await fixture.ScheduleAsync(new TestWorkflowBuilder(name, fixture.MockBaseUrl, "2.0.0")
            .HttpGet("valid-activity", "/success").Build());

        await WaitForExecutionInList(v1.Id, TimeSpan.FromSeconds(5));
        await WaitForExecutionInList(v2.Id, TimeSpan.FromSeconds(5));

        // Act + Assert - version pins to a single version
        var v1Only = await fixture.DaemonClient.ListExecutionsAsync(
            new ListExecutionsRequest { Workflow = new WorkflowReference { Name = name, Version = "1.0.0" } });
        Assert.Contains(v1Only.Executions, e => e.Id == v1.Id);
        Assert.DoesNotContain(v1Only.Executions, e => e.Id == v2.Id);
        Assert.All(v1Only.Executions, e => Assert.Equal("1.0.0", e.Workflow.Version));

        // Act + Assert - omitting version matches all loaded versions
        var allVersions = await fixture.DaemonClient.ListExecutionsAsync(
            new ListExecutionsRequest { Workflow = new WorkflowReference { Name = name } });
        Assert.Contains(allVersions.Executions, e => e.Id == v1.Id);
        Assert.Contains(allVersions.Executions, e => e.Id == v2.Id);
    }

    [Fact]
    public async Task ListExecutions_FiltersByDigest_AndResponseEchoesDigest()
    {
        // Arrange
        var testId = Guid.NewGuid().ToString("N")[..8];
        var name = $"filter-digest-{testId}";

        var scheduled = await fixture.ScheduleAsync(new TestWorkflowBuilder(name, fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success").Build());
        var summary = await WaitForExecutionInList(scheduled.Id, TimeSpan.FromSeconds(5));

        // The response now echoes the resolved bundle digest.
        Assert.NotNull(summary);
        Assert.False(string.IsNullOrEmpty(summary!.Workflow.Digest));
        var digest = summary.Workflow.Digest;

        // Act - filter by that exact digest
        var response = await fixture.DaemonClient.ListExecutionsAsync(
            new ListExecutionsRequest { Workflow = new WorkflowReference { Name = name, Digest = digest } });

        // Assert
        Assert.Contains(response.Executions, e => e.Id == scheduled.Id);
        Assert.All(response.Executions, e => Assert.Equal(digest, e.Workflow.Digest));
    }

    [Fact]
    public async Task ListExecutions_WithFilterButEmptyName_ReturnsInvalidArgument()
    {
        var call = fixture.DaemonClient.ListExecutionsAsync(
            new ListExecutionsRequest { Workflow = new WorkflowReference { Name = "" } });

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    /// <summary>
    /// Helper to wait for an execution to appear in list
    /// </summary>
    private async Task<ExecutionSummary?> WaitForExecutionInList(
        string executionId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
            var execution = response.Executions.FirstOrDefault(e => e.Id == executionId);

            if (execution != null)
                return execution;

            await Task.Delay(100); // Poll every 100ms
        }

        return null;
    }

    /// <summary>
    /// Helper to wait for an execution to reach a specific status
    /// </summary>
    private async Task<ExecutionSummary?> WaitForExecutionWithStatus(
        string executionId,
        ExecutionStatus expectedStatus,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
            var execution = response.Executions.FirstOrDefault(e => e.Id == executionId);

            if (execution?.Status == expectedStatus)
                return execution;

            await Task.Delay(100); // Poll every 100ms
        }

        return null;
    }

    /// <summary>
    /// Helper to wait for an execution to reach at least a minimum status (status >= minimum)
    /// </summary>
    private async Task<ExecutionSummary?> WaitForExecutionWithMinimumStatus(
        string executionId,
        ExecutionStatus minimumStatus,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
            var execution = response.Executions.FirstOrDefault(e => e.Id == executionId);

            if (execution != null && execution.Status >= minimumStatus)
                return execution;

            await Task.Delay(100); // Poll every 100ms
        }

        return null;
    }
}
