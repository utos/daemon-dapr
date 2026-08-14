using Grpc.Core;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Cancellation end to end: RPC, orchestration teardown, event, projection, read model.
///
/// The workflow under test is a timer loop with no terminating rule — the intentionally endless
/// poller the spec describes, and the exact thing that had no way to be stopped before.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class CancelExecutionTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task CancelExecution_ThrowsInvalidArgument_WhenIdIsInvalid()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.CancelExecutionAsync(
                new CancelExecutionRequest { ExecutionId = "not-a-valid-guid" }));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task CancelExecution_ThrowsNotFound_WhenExecutionDoesNotExist()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.CancelExecutionAsync(
                new CancelExecutionRequest { ExecutionId = Guid.NewGuid().ToString() }));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact(Skip = SkipReasons.DaprDropsTerminationsSharingAWorkItem)]
    public async Task CancelExecution_StopsAnEndlessPoller_AndRecordsTheReason()
    {
        // The poller calls a stub each lap, so the run's own laps are observable from out here.
        // Asserting only on the recorded status would pass against a cancellation that recorded
        // itself and stopped nothing — which is exactly the failure this test is named for, and
        // exactly what it used to miss.
        const string path = "/cancel-endless-poller";
        StubJson(path);

        var request = new TestWorkflowBuilder("cancel-endless-poller", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .OnSuccess("wait")
            .Timer("wait", TimeSpan.FromSeconds(1))
            .OnSuccess("poll")          // back-edge with no exit: runs until cancelled
            .Build("poll");

        var scheduled = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduled.Id, ExecutionStatus.Active, TimeSpan.FromSeconds(20));

        var response = await fixture.DaemonClient.CancelExecutionAsync(
            new CancelExecutionRequest { ExecutionId = scheduled.Id, Reason = "no longer needed" });

        Assert.Equal(ExecutionStatus.Cancelled, response.Status);

        // Three laps of a one-second timer: a run still going would certainly have polled again.
        var lapsAtCancellation = CallCount(path);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(lapsAtCancellation, CallCount(path));

        await WaitForStatus(scheduled.Id, ExecutionStatus.Cancelled, TimeSpan.FromSeconds(20));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Cancelled, details.Summary.Status);
        Assert.Equal("no longer needed", details.Summary.CancellationReason);

        // A deliberate stop is not a failure, and must never be reported as one.
        Assert.Null(details.Summary.Error);

        // Every terminal state finishes a run.
        Assert.NotNull(details.Summary.FinishedAt);
    }

    [Fact(Skip = SkipReasons.DaprDropsTerminationsSharingAWorkItem)]
    public async Task CancelExecution_IsIdempotent()
    {
        var request = new TestWorkflowBuilder("cancel-twice", fixture.MockBaseUrl)
            .Timer("wait", TimeSpan.FromSeconds(1))
            .OnSuccess("wait")
            .Build();

        var scheduled = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduled.Id, ExecutionStatus.Active, TimeSpan.FromSeconds(20));

        await fixture.DaemonClient.CancelExecutionAsync(
            new CancelExecutionRequest { ExecutionId = scheduled.Id, Reason = "first" });
        await WaitForStatus(scheduled.Id, ExecutionStatus.Cancelled, TimeSpan.FromSeconds(20));

        // The caller is asking for a state the run is already in, so this succeeds rather than
        // reporting a precondition failure.
        var second = await fixture.DaemonClient.CancelExecutionAsync(
            new CancelExecutionRequest { ExecutionId = scheduled.Id, Reason = "second" });

        Assert.Equal(ExecutionStatus.Cancelled, second.Status);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        // The first cancellation is the one that happened; the second did not overwrite it.
        Assert.Equal("first", details.Summary.CancellationReason);
    }

    [Fact]
    public async Task CancelExecution_ThrowsFailedPrecondition_WhenTheRunAlreadyCompleted()
    {
        // The first terminal state wins. A late cancel must not rewrite a successful run.
        var request = new TestWorkflowBuilder("cancel-after-completion", fixture.MockBaseUrl)
            .HttpGet("only", "/success")
            .Build();

        var scheduled = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduled.Id, ExecutionStatus.Completed, TimeSpan.FromSeconds(20));

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.CancelExecutionAsync(
                new CancelExecutionRequest { ExecutionId = scheduled.Id }));

        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
    }

    [Fact(Skip = SkipReasons.DaprDropsTerminationsSharingAWorkItem)]
    public async Task A_cancelled_execution_can_be_deleted()
    {
        // DeleteExecution requires a terminal execution, and cancellation is one — otherwise a
        // cancelled run would be undeletable, which is the state delete told callers to reach.
        var request = new TestWorkflowBuilder("cancel-then-delete", fixture.MockBaseUrl)
            .Timer("wait", TimeSpan.FromSeconds(1))
            .OnSuccess("wait")
            .Build();

        var scheduled = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduled.Id, ExecutionStatus.Active, TimeSpan.FromSeconds(20));

        await fixture.DaemonClient.CancelExecutionAsync(
            new CancelExecutionRequest { ExecutionId = scheduled.Id });
        await WaitForStatus(scheduled.Id, ExecutionStatus.Cancelled, TimeSpan.FromSeconds(20));

        await fixture.DaemonClient.DeleteExecutionAsync(
            new DeleteExecutionRequest { ExecutionId = scheduled.Id });

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = scheduled.Id }));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    private int CallCount(string path) =>
        fixture.WireMockServer.LogEntries.Count(entry => entry.RequestMessage.Path == path);

    private void StubJson(string path) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));

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

        throw new TimeoutException(
            $"Execution {executionId} did not reach {expected} within {timeout}.");
    }
}
