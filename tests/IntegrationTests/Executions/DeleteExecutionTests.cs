using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

[Collection(DaemonTestCollection.Name)]
public class DeleteExecutionTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task DeleteExecution_ThrowsInvalidArgument_WhenIdIsInvalid()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.DeleteExecutionAsync(
                new DeleteExecutionRequest { ExecutionId = "not-a-valid-guid" }));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task DeleteExecution_ThrowsNotFound_WhenExecutionDoesNotExist()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.DeleteExecutionAsync(
                new DeleteExecutionRequest { ExecutionId = Guid.NewGuid().ToString() }));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task DeleteExecution_RemovesCompletedExecution()
    {
        // Arrange
        var request = new TestWorkflowBuilder("delete-completed-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduleResponse.Id, ExecutionStatus.Completed, TimeSpan.FromSeconds(15));

        // Act
        await fixture.DaemonClient.DeleteExecutionAsync(
            new DeleteExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert — gone from Get and List
        var getException = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = scheduleResponse.Id }));
        Assert.Equal(StatusCode.NotFound, getException.StatusCode);

        var list = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
        Assert.DoesNotContain(list.Executions, e => e.Id == scheduleResponse.Id);
    }

    [Fact]
    public async Task DeleteExecution_RemovesFailedExecution()
    {
        // Arrange — an endpoint that fails so the execution reaches a terminal FAILED state
        var failPath = "/delete-failed-test";
        fixture.WireMockServer
            .Given(Request.Create().WithPath(failPath).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error": "Internal Server Error"}"""));

        var request = new TestWorkflowBuilder("delete-failed-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", failPath)
            .Build();

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduleResponse.Id, ExecutionStatus.Failed, TimeSpan.FromSeconds(15));

        // Act
        await fixture.DaemonClient.DeleteExecutionAsync(
            new DeleteExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert
        var getException = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = scheduleResponse.Id }));
        Assert.Equal(StatusCode.NotFound, getException.StatusCode);
    }

    [Fact]
    public async Task DeleteExecution_ThrowsFailedPrecondition_WhenExecutionNotTerminal()
    {
        // Arrange — a long durable timer keeps the execution ACTIVE (non-terminal) for the test window
        var request = new TestWorkflowBuilder("delete-nonterminal-test", fixture.MockBaseUrl)
            .Timer("wait", TimeSpan.FromSeconds(30))
            .Build();

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForNonTerminal(scheduleResponse.Id, TimeSpan.FromSeconds(15));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.DeleteExecutionAsync(
                new DeleteExecutionRequest { ExecutionId = scheduleResponse.Id }));
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);

        // The execution must still exist — a rejected delete deletes nothing.
        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduleResponse.Id });
        Assert.Equal(scheduleResponse.Id, details.Summary.Id);
    }

    [Fact]
    public async Task DeleteExecution_WithNonDetachedChild_DeletesTheParent()
    {
        // Arrange — parent invokes a non-detached child; the whole run completes as one unit.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/delete-cascade-child").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("delete-cascade-parent", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "delete-cascade-child-wf", "child-step")
            .OnSuccess("end")
            .AddDependentWorkflow("delete-cascade-child-wf",
                child => { child.HttpGet("child-step", "/delete-cascade-child"); })
            .Build("call-child");

        var scheduleResponse = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduleResponse.Id, ExecutionStatus.Completed, TimeSpan.FromSeconds(30));

        // Act — there is nothing to cascade to: an awaited sub-workflow is a Dapr child
        // orchestration, not an execution of its own, so it has no read-model row and no stream.
        // Deleting the parent removes the one record the run produced.
        await fixture.DaemonClient.DeleteExecutionAsync(
            new DeleteExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert — parent is gone
        var getException = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = scheduleResponse.Id }));
        Assert.Equal(StatusCode.NotFound, getException.StatusCode);

        var list = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
        Assert.DoesNotContain(list.Executions, e => e.Id == scheduleResponse.Id);
    }

    [Fact]
    public async Task DeleteExecution_DoesNotAffectStillRunningDetachedSubWorkflow()
    {
        // Arrange — a detached child is an independent top-level execution (ParentExecutionId is
        // null, so the cascade never picks it up). The child sits behind a durable timer, so when
        // the parent completes the child is still mid-flight. We then delete the parent and prove
        // the detached child continues as-is: it keeps running and completes its own work.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/delete-detached-child").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"done": true}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/delete-detached-parent-after").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));

        var handleToInput = new Struct();
        handleToInput.Fields.Add("childId", Value.ForString("{{ output.execution_id }}"));

        var request = new TestWorkflowBuilder("delete-detached-parent", fixture.MockBaseUrl)
            .WorkflowCall("start-child", "delete-detached-child-wf", "wait", detached: true)
            .OnSuccess("after", handleToInput)
            .HttpPost("after", "/delete-detached-parent-after", """{"childId": "{{ input.childId }}"}""")
            .OnSuccess("end")
            .AddDependentWorkflow("delete-detached-child-wf", child =>
            {
                // The timer keeps the child running well past the parent's completion.
                child.Timer("wait", TimeSpan.FromSeconds(6)).OnSuccess("hit");
                child.HttpGet("hit", "/delete-detached-child");
            })
            .Build("start-child");

        var scheduleResponse = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = scheduleResponse.Id
        });
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        await WaitForStatus(scheduleResponse.Id, ExecutionStatus.Completed, TimeSpan.FromSeconds(15));

        // Read the detached child's id from the handle the parent posted.
        var postCall = fixture.WireMockServer.LogEntries
            .First(e => e.RequestMessage.Path == "/delete-detached-parent-after");
        var childId = JsonDocument.Parse(postCall.RequestMessage.Body!)
            .RootElement.GetProperty("childId").GetString();
        Assert.False(string.IsNullOrEmpty(childId));

        // The child is genuinely still in flight: its timer has not fired, so its endpoint is unhit
        // and it is not yet terminal. This is what makes the delete-while-running case meaningful.
        Assert.DoesNotContain(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/delete-detached-child");
        var childBeforeDelete = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = childId });
        Assert.True(childBeforeDelete.Summary.Status is not (ExecutionStatus.Completed or ExecutionStatus.Failed),
            $"Child should still be running before the parent delete, but was {childBeforeDelete.Summary.Status}");

        // Act — delete the (terminal) parent while the detached child is still running.
        await fixture.DaemonClient.DeleteExecutionAsync(
            new DeleteExecutionRequest { ExecutionId = scheduleResponse.Id });

        // Assert — parent is gone, child still exists...
        var getParent = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = scheduleResponse.Id }));
        Assert.Equal(StatusCode.NotFound, getParent.StatusCode);

        var childAfterDelete = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = childId });
        Assert.Equal(childId, childAfterDelete.Summary.Id);

        // ...and, crucially, the child continues as-is: it runs to completion and does its own work
        // (its endpoint is hit) after the parent was deleted.
        await WaitForStatus(childId!, ExecutionStatus.Completed, TimeSpan.FromSeconds(30));
        Assert.Contains(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/delete-detached-child");

        var list = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
        Assert.Contains(list.Executions, e => e.Id == childId);
        Assert.DoesNotContain(list.Executions, e => e.Id == scheduleResponse.Id);
    }

    private async Task WaitForStatus(string executionId, ExecutionStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var details = await fixture.DaemonClient.GetExecutionAsync(
                    new GetExecutionRequest { ExecutionId = executionId });
                if (details.Summary.Status == expected)
                    return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // Projection not caught up yet.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Execution {executionId} did not reach {expected} within {timeout}");
    }

    private async Task WaitForNonTerminal(string executionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var details = await fixture.DaemonClient.GetExecutionAsync(
                    new GetExecutionRequest { ExecutionId = executionId });
                if (details.Summary.Status is ExecutionStatus.Scheduled or ExecutionStatus.Active)
                    return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // Projection not caught up yet.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Execution {executionId} did not reach a non-terminal state within {timeout}");
    }
}
