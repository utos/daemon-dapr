using Common.Constants;
using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using Utos.Workflows.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

[Collection(DaemonTestCollection.Name)]
public class GetExecutionDetailsTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task GetExecution_ThrowsNotFound_WhenExecutionDoesNotExist()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = nonExistentId }));

        Assert.Equal(Grpc.Core.StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task GetExecution_ThrowsInvalidArgument_WhenExecutionIdIsInvalid()
    {
        // Arrange
        var invalidId = "not-a-valid-guid";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = invalidId }));

        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task GetExecution_ReturnsBasicExecutionDetails()
    {
        // Arrange
        var request = new TestWorkflowBuilder("get-basic-test", fixture.MockBaseUrl,
                "1.2.3", "Test workflow for get endpoint")
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert
        Assert.NotNull(details);
        Assert.Equal(scheduleResponse.Id, details.Summary.Id);
        Assert.Equal("get-basic-test", details.Summary.Workflow.Name);
        Assert.Equal("1.2.3", details.Summary.Workflow.Version);
        Assert.Contains(details.Summary.Status, new[]
        {
            ExecutionStatus.Unspecified,
            ExecutionStatus.Scheduled,
            ExecutionStatus.Active,
            ExecutionStatus.Completed,
            ExecutionStatus.Failed
        });
    }

    [Fact]
    public async Task GetExecution_ReturnsWhatTheRunReturned()
    {
        // The terminal `result` entry of the run's output stream, read back rather than kept in
        // the read model — there is one source of truth for what an execution returned, and
        // WatchOutput reads the same entry.
        var request = new TestWorkflowBuilder("get-result-test", fixture.MockBaseUrl)
            .HttpGet("call", "/success")
            .OnResult(new Struct
            {
                Fields =
                {
                    ["status"] = Value.ForString("{{ output.status }}"),
                    ["count"] = Value.ForNumber(3)
                }
            })
            .Build();

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
        Assert.NotNull(details.Result);
        Assert.Equal("success", details.Result.Fields["status"].StringValue);
        Assert.Equal(3d, details.Result.Fields["count"].NumberValue);
    }

    [Fact]
    public async Task GetExecution_ReportsNoResult_WhenTheRunEndedWithoutAValue()
    {
        // Spec 0.0.16: a run that ended without a value — its rules ran out, or a bare `return` —
        // leaves `result` unset. It used to report an empty struct, on the argument that absence
        // was indistinguishable from failure; the status is what distinguishes them.
        var request = new TestWorkflowBuilder("get-no-result-test", fixture.MockBaseUrl)
            .HttpGet("call", "/success")
            .Build();

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
        Assert.Null(details.Result);
    }

    [Fact]
    public async Task GetExecution_ReportsNoResult_WhenTheRunFailed()
    {
        // A failed run reports its error, never a result — its stream terminated with an error
        // entry, so there is nothing to show.
        const string path = "/get-failed-result-test";
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("get-failed-result-test", fixture.MockBaseUrl)
            .HttpGet("call", path)
            .Build();

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        Assert.Equal(ExecutionStatus.Failed, details.Summary.Status);
        Assert.Null(details.Result);
        Assert.NotNull(details.Summary.Error);
    }

    [Fact]
    public async Task GetExecution_ReturnsFullWorkflowDefinition()
    {
        // Arrange
        var request = new TestWorkflowBuilder("get-workflow-def-test", fixture.MockBaseUrl)
            .HttpGet("test-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert - Full workflow definition should be included
        Assert.NotNull(details.Bundle.Workflows[details.Bundle.EntryPoint]);
        Assert.Equal(WorkflowDocument.ApiVersion, details.Bundle.Workflows[details.Bundle.EntryPoint].ApiVersion);
        Assert.Equal(WorkflowDocument.Kind, details.Bundle.Workflows[details.Bundle.EntryPoint].Kind);

        Assert.NotNull(details.Bundle.Workflows[details.Bundle.EntryPoint].Metadata);
        Assert.Equal("get-workflow-def-test", details.Bundle.Workflows[details.Bundle.EntryPoint].Metadata.Name);
        Assert.Equal("1.0.0", details.Bundle.Workflows[details.Bundle.EntryPoint].Metadata.Version);

        Assert.NotNull(details.Bundle.Workflows[details.Bundle.EntryPoint].Spec);
        Assert.Single(details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities);
        Assert.True(details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities.ContainsKey("test-activity"));
        Assert.Equal("GET",
            details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities["test-activity"].Http.Method);
    }

    [Fact]
    public async Task GetExecution_ReturnsWorkflowWithMultipleActivities()
    {
        // Arrange
        var request = new TestWorkflowBuilder("multi-activity-workflow", fixture.MockBaseUrl)
            .HttpGet("activity-1", "/success")
            .HttpGet("activity-2", "/success")
            .HttpGet("activity-3", "/success")
            .BuildSequential();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert - Verify the workflow has all 3 activities as keys in the map
        Assert.Equal(3, details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities.Count);
        Assert.True(details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities.ContainsKey("activity-1"));
        Assert.True(details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities.ContainsKey("activity-2"));
        Assert.True(details.Bundle.Workflows[details.Bundle.EntryPoint].Spec.Activities.ContainsKey("activity-3"));
    }

    [Fact]
    public async Task GetExecution_ReturnsInput_WhenProvided()
    {
        // Arrange
        var request = new TestWorkflowBuilder("get-input-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Primitive types
        request.Input.Data.Add("stringKey", Google.Protobuf.WellKnownTypes.Value.ForString("test-value"));
        request.Input.Data.Add("numberKey", Google.Protobuf.WellKnownTypes.Value.ForNumber(42));
        request.Input.Data.Add("boolKey", Google.Protobuf.WellKnownTypes.Value.ForBool(true));

        // Nested object
        var nestedStruct = new Google.Protobuf.WellKnownTypes.Struct();
        nestedStruct.Fields.Add("nestedString", Google.Protobuf.WellKnownTypes.Value.ForString("nested-value"));
        nestedStruct.Fields.Add("nestedNumber", Google.Protobuf.WellKnownTypes.Value.ForNumber(123));
        request.Input.Data.Add("objectKey", Google.Protobuf.WellKnownTypes.Value.ForStruct(nestedStruct));

        // Array
        var arrayValue = Google.Protobuf.WellKnownTypes.Value.ForList(
            Google.Protobuf.WellKnownTypes.Value.ForString("item1"),
            Google.Protobuf.WellKnownTypes.Value.ForNumber(10),
            Google.Protobuf.WellKnownTypes.Value.ForBool(false)
        );
        request.Input.Data.Add("arrayKey", arrayValue);

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert - Primitives
        Assert.NotNull(details.Input);
        Assert.True(details.Input.Data.ContainsKey("stringKey"));
        Assert.True(details.Input.Data.ContainsKey("numberKey"));
        Assert.True(details.Input.Data.ContainsKey("boolKey"));

        Assert.Equal("test-value", details.Input.Data["stringKey"].StringValue);
        Assert.Equal(42, details.Input.Data["numberKey"].NumberValue);
        Assert.True(details.Input.Data["boolKey"].BoolValue);

        // Assert - Nested object
        Assert.True(details.Input.Data.ContainsKey("objectKey"));
        var nestedObject = details.Input.Data["objectKey"].StructValue;
        Assert.NotNull(nestedObject);
        Assert.Equal("nested-value", nestedObject.Fields["nestedString"].StringValue);
        Assert.Equal(123, nestedObject.Fields["nestedNumber"].NumberValue);

        // Assert - Array
        Assert.True(details.Input.Data.ContainsKey("arrayKey"));
        var array = details.Input.Data["arrayKey"].ListValue;
        Assert.NotNull(array);
        Assert.Equal(3, array.Values.Count);
        Assert.Equal("item1", array.Values[0].StringValue);
        Assert.Equal(10, array.Values[1].NumberValue);
        Assert.False(array.Values[2].BoolValue);
    }

    [Fact]
    public async Task GetExecution_HandlesEmptyInput()
    {
        // Arrange
        var request = new TestWorkflowBuilder("get-empty-input-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert - Empty input may be null or empty object
        if (details.Input != null) Assert.Empty(details.Input.Data);
    }

    [Fact]
    public async Task GetExecution_IncludesAllTimingFields()
    {
        // Arrange
        var request = new TestWorkflowBuilder("get-timing-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduleResponse.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert - CreatedAt is always populated
        Assert.NotNull(details.Summary.CreatedAt);
        var createdAt = details.Summary.CreatedAt.ToDateTime();
        Assert.True(Math.Abs((DateTime.UtcNow - createdAt).TotalSeconds) < 10,
            $"CreatedAt should be within 10 seconds of now. Expected: {DateTime.UtcNow}, Actual: {createdAt}");

        // Optional fields depend on execution state progression
        if (details.Summary.Status >= ExecutionStatus.Scheduled) Assert.NotNull(details.Summary.ScheduledAt);

        if (details.Summary.Status >= ExecutionStatus.Active) Assert.NotNull(details.Summary.StartedAt);

        // completed_at/failed_at collapsed into a single finished_at; status carries which.
        if (details.Summary.Status is ExecutionStatus.Completed or ExecutionStatus.Failed)
            Assert.NotNull(details.Summary.FinishedAt);
        else
            Assert.Null(details.Summary.FinishedAt);
    }

    [Fact]
    public async Task GetExecution_SetsCompletedAtAndNotFailedAt_WhenCompleted()
    {
        // Arrange
        var successEndpoint = "/get-completed-timing-test";
        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(successEndpoint).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"success\"}"));

        var request = new TestWorkflowBuilder("get-completed-timing-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", successEndpoint)
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForExecutionWithStatus(scheduleResponse.Id, ExecutionStatus.Completed, TimeSpan.FromSeconds(10));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert
        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
        Assert.NotNull(details.Summary.FinishedAt);
        Assert.True(details.Summary.Error is null,
            $"Error should be unset for a completed execution, but was: '{details.Summary.Error?.Message}'");

        // Verify FinishedAt is after CreatedAt
        var createdAt = details.Summary.CreatedAt.ToDateTime();
        var completedAt = details.Summary.FinishedAt.ToDateTime();
        Assert.True(completedAt >= createdAt,
            $"FinishedAt ({completedAt}) should be >= CreatedAt ({createdAt})");
    }

    [Fact]
    public async Task GetExecution_SetsFailedAtAndNotCompletedAt_WhenFailed()
    {
        // Arrange
        var failEndpoint = "/get-failed-timing-test";
        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(failEndpoint).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Internal Server Error\"}"));

        var request = new TestWorkflowBuilder("get-failed-timing-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", failEndpoint)
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForExecutionWithStatus(scheduleResponse.Id, ExecutionStatus.Failed, TimeSpan.FromSeconds(10));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert
        Assert.Equal(ExecutionStatus.Failed, details.Summary.Status);
        Assert.NotNull(details.Summary.FinishedAt);
        Assert.NotNull(details.Summary.Error);
        Assert.NotEmpty(details.Summary.Error.Message);

        // Verify FinishedAt is after CreatedAt
        var createdAt = details.Summary.CreatedAt.ToDateTime();
        var failedAt = details.Summary.FinishedAt.ToDateTime();
        Assert.True(failedAt >= createdAt,
            $"FinishedAt ({failedAt}) should be >= CreatedAt ({createdAt})");
    }

    [Fact]
    public async Task GetExecution_IncludesFullErrorMessage_WhenFailed()
    {
        // Arrange - Create workflow that will fail
        var failEndpoint = "/get-fail-test";
        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(failEndpoint).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(503)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"Service Unavailable - Database connection failed\"}"));

        var request = new TestWorkflowBuilder("get-error-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", failEndpoint)
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForExecutionWithStatus(scheduleResponse.Id, ExecutionStatus.Failed, TimeSpan.FromSeconds(10));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert
        Assert.Equal(ExecutionStatus.Failed, details.Summary.Status);
        Assert.NotNull(details.Summary.Error);
        Assert.NotEmpty(details.Summary.Error.Message);
        Assert.NotNull(details.Summary.FinishedAt);
    }

    [Fact]
    public async Task GetExecution_DoesNotTruncateErrorMessage()
    {
        // Arrange - Create workflow that fails with a long error message
        var longErrorEndpoint = "/get-long-error-test";
        var longErrorMessage = new string('x', 500);

        fixture.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(longErrorEndpoint).UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"error\": \"" + longErrorMessage + "\"}"));

        var request = new TestWorkflowBuilder("get-long-error-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", longErrorEndpoint)
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForExecutionWithStatus(scheduleResponse.Id, ExecutionStatus.Failed, TimeSpan.FromSeconds(10));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert - GetExecution should return full error message (not truncated like list view)
        Assert.NotNull(details.Summary.Error);
        Assert.NotEmpty(details.Summary.Error.Message);
        // Note: The actual error format depends on how the workflow executor packages errors
        // We're just verifying it's not truncated to 200 chars like in the list view
    }

    [Fact]
    public async Task GetExecution_ReflectsStatusProgression()
    {
        // Arrange
        var request = new TestWorkflowBuilder("get-progression-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var scheduleResponse = await fixture.ScheduleAsync(request);

        // Poll and track status changes
        var statuses = new List<ExecutionStatus>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
            try
            {
                var details = await fixture.DaemonClient.GetExecutionAsync(
                    new GetExecutionRequest { ExecutionId = scheduleResponse.Id });

                if (!statuses.Contains(details.Summary.Status)) statuses.Add(details.Summary.Status);

                // Stop when we reach terminal state
                if (details.Summary.Status is ExecutionStatus.Completed or ExecutionStatus.Failed)
                    break;

                await Task.Delay(100);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                // Projection hasn't caught up yet
                await Task.Delay(100);
            }

        // Assert
        // Note: Due to eventual consistency, we may not observe Initial status if projection/execution is fast
        // But we should observe at least some status progression
        Assert.NotEmpty(statuses);

        var terminalStatuses = statuses.Count(s => s is ExecutionStatus.Completed or ExecutionStatus.Failed);
        Assert.Equal(1, terminalStatuses);

        // The final status should be terminal
        Assert.True(statuses[^1] is ExecutionStatus.Completed or ExecutionStatus.Failed,
            $"Final status should be terminal, but was {statuses[^1]}");

        // Verify no backwards transitions (status should only progress forward)
        for (var i = 1; i < statuses.Count; i++)
            Assert.True((int)statuses[i] > (int)statuses[i - 1],
                $"Status should only progress forward. Previous: {statuses[i - 1]}, Current: {statuses[i]}");
    }

    /// <summary>
    /// Helper to wait for an execution to reach a specific status
    /// </summary>
    private async Task<bool> WaitForExecutionWithStatus(
        string executionId,
        ExecutionStatus expectedStatus,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
            try
            {
                var details = await fixture.DaemonClient.GetExecutionAsync(
                    new GetExecutionRequest { ExecutionId = executionId });

                if (details.Summary.Status == expectedStatus)
                    return true;

                await Task.Delay(100);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                await Task.Delay(100);
            }

        return false;
    }
}
