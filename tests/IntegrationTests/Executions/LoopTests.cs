using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for back-edge transitions — a transition target naming an already-visited activity,
/// which forms a loop. The executor bounds one thing and one thing only: it continues as new once
/// a loop is detected, so Dapr instance history stays bounded. How many times a loop goes round is
/// not bounded at all — an endless loop is legal, and cancellation is what stops one.
///
/// Loops here terminate on <em>data</em>, so no timer or cancellation primitive is needed.
/// State is accumulated as a string, which exercises the same threading as a counter without
/// depending on value typing; the numeric equivalent is covered by
/// <see cref="TemplateTypingTests.WholeExpression_KeepsNumber_SoCountersAccumulateAcrossALoop"/>.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class LoopTests(DaemonTestFixture fixture)
{
    private const string Seed = "s";

    /// <summary>Marker value the loop accumulates to after <paramref name="laps"/> back-edges.</summary>
    private static string MarkerAfter(int laps) => Seed + new string('x', laps);

    [Fact]
    public async Task BackEdge_LoopsUntilConditionIsFalse()
    {
        // Arrange - poll -> poll until the accumulated marker reaches its target
        const string path = "/loop-basic";
        StubJson(path);

        const int laps = 3;
        var request = new TestWorkflowBuilder("loop-basic-workflow", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .WithHeader("X-Marker", "{{ input.marker }}")
            .OnSuccess("poll", Append("marker"), when: NotYetAt(laps))
            .OnEnd()
            .WithInput("marker", Seed)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        var events = await WatchToCompletion(response.Id);

        // Assert - one execution per lap plus the final pass that exits
        Assert.Equal(laps + 1, CallCount(path));
        await AssertCompleted(response.Id);
        Assert.Contains(events, e => e.Message.Contains("Loop detected at activity 'poll'"));
    }

    [Fact]
    public async Task BackEdge_ThreadsAccumulatingStateAcrossTheLoop()
    {
        // Arrange - the marker is carried by the back-edge's input transform, so each pass
        // must observe the previous pass's value
        const string path = "/loop-state";
        StubJson(path);

        const int laps = 3;
        var request = new TestWorkflowBuilder("loop-state-workflow", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .WithHeader("X-Marker", "{{ input.marker }}")
            .OnSuccess("poll", Append("marker"), when: NotYetAt(laps))
            .OnEnd()
            .WithInput("marker", Seed)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert - the marker grew by one character per pass
        Assert.Equal(
            new[] { MarkerAfter(0), MarkerAfter(1), MarkerAfter(2), MarkerAfter(3) },
            MarkersSent(path));
    }

    [Fact]
    public async Task Loop_LongerThanContinueAsNewThreshold_StillCompletes()
    {
        // Arrange - 20 laps against a threshold of 5 transitions per segment, so the run
        // spans several Dapr instances and the visited set and counters must survive each
        const string path = "/loop-continued";
        StubJson(path);

        const int laps = 20;
        var request = new TestWorkflowBuilder("loop-continued-workflow", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .WithHeader("X-Marker", "{{ input.marker }}")
            .OnSuccess("poll", Append("marker"), when: NotYetAt(laps))
            .OnEnd()
            .WithInput("marker", Seed)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        var events = await WatchToCompletion(response.Id);

        // Assert - every lap ran, and the loop still exited at exactly the right marker
        Assert.Equal(laps + 1, CallCount(path));
        Assert.Equal(MarkerAfter(laps), MarkersSent(path).Last());
        await AssertCompleted(response.Id);
        Assert.Contains(events, e => e.Message.Contains("Continuing as new"));
    }

    [Fact]
    public async Task Loop_WithMultiActivityBody_StillCompletes()
    {
        // Arrange - a 3-activity loop body fills Dapr history three times faster per lap, so
        // this pins down that continuation is measured in transitions rather than in laps
        const string path = "/loop-body";
        StubJson(path);

        const int laps = 3;
        var request = new TestWorkflowBuilder("loop-body-workflow", fixture.MockBaseUrl)
            .HttpGet("step-a", path)
            .WithHeader("X-Marker", "{{ input.marker }}")
            .OnSuccess("step-b", Carry("marker"))
            .HttpGet("step-b", path)
            .OnSuccess("step-c", Carry("marker"))
            .HttpGet("step-c", path)
            .OnSuccess("step-a", Append("marker"), when: NotYetAt(laps))
            .OnEnd()
            .WithInput("marker", Seed)
            .Build("step-a");

        // Act
        var response = await fixture.ScheduleAsync(request);
        var events = await WatchToCompletion(response.Id);

        // Assert - three activities per pass, laps + 1 passes
        Assert.Equal(3 * (laps + 1), CallCount(path));
        await AssertCompleted(response.Id);
        Assert.Contains(events, e => e.Message.Contains("Continuing as new"));
    }

    [Fact]
    public async Task Loop_CallingSubWorkflow_GivesEachCallAFreshBudget()
    {
        // Arrange - the child is invoked once per lap. Each invocation is its own execution,
        // so it must start with an empty visited set; inheriting the parent's would make the
        // second call look like a back-edge.
        const string path = "/loop-child";
        StubJson(path);

        const int laps = 3;
        var request = new TestWorkflowBuilder("loop-parent-workflow", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "loop-child-workflow", "child-step")
            .OnSuccess("call-child", Append("marker"), when: NotYetAt(laps))
            .OnEnd()
            .AddDependentWorkflow("loop-child-workflow",
                child => { child.HttpGet("child-step", path); })
            .WithInput("marker", Seed)
            .Build("call-child");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert
        Assert.Equal(laps + 1, CallCount(path));
        await AssertCompleted(response.Id);
    }

    [Fact]
    public async Task Loop_WithNoExit_KeepsRunningUntilItIsCancelled()
    {
        // Arrange - an unconditional back-edge, so nothing ever leaves the loop. This used to
        // fail the run past a ceiling of looped activity executions. That ceiling existed only
        // because the daemon had no way to stop a runaway; CancelExecution is that way, so a
        // loop with no exit is now exactly what the spec says it is — a run that continues until
        // someone stops it. A timer paces the lap so the stub is not hammered.
        const string path = "/loop-runaway";
        StubJson(path);

        var request = new TestWorkflowBuilder("loop-runaway-workflow", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .OnSuccess("pace")
            .Timer("pace", TimeSpan.FromSeconds(1))
            .OnSuccess("poll")
            .Build("poll");

        var response = await fixture.ScheduleAsync(request);

        // Act - let it lap well past where the old ceiling would have been irrelevant, then stop it
        await WaitForLaps(path, 3, TimeSpan.FromSeconds(30));

        await fixture.DaemonClient.CancelExecutionAsync(
            new CancelExecutionRequest { ExecutionId = response.Id, Reason = "runaway" });

        // Assert - stopped, and never failed on its own. Polled rather than read once: the RPC
        // returns when the event is appended, and the read model is a projection behind it.
        var execution = await WaitForStatus(
            response.Id, ExecutionStatus.Cancelled, TimeSpan.FromSeconds(20));

        Assert.Null(execution.Summary.Error);
    }

    [Fact]
    public async Task LinearWorkflow_LongerThanThreshold_DoesNotContinueAsNew()
    {
        // Arrange - 7 sequential activities, above the threshold of 5. Continuation is gated
        // on loop detection precisely so a non-looping run keeps its full Dapr history.
        const string path = "/loop-linear";
        StubJson(path);

        var builder = new TestWorkflowBuilder("linear-long-workflow", fixture.MockBaseUrl);
        for (var i = 0; i < 7; i++)
            builder.HttpGet($"step-{i}", path);
        var request = builder.BuildSequential();

        // Act
        var response = await fixture.ScheduleAsync(request);
        var events = await WatchToCompletion(response.Id);

        // Assert
        await AssertCompleted(response.Id);
        Assert.DoesNotContain(events, e => e.Message.Contains("Continuing as new"));
        Assert.DoesNotContain(events, e => e.Message.Contains("Loop detected"));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    /// <summary>Transform that carries a value across a transition unchanged.</summary>
    private static Struct Carry(string field)
    {
        var transform = new Struct();
        transform.Fields.Add(field, Value.ForString($"{{{{ input.{field} }}}}"));
        return transform;
    }

    /// <summary>Transform that appends one character, accumulating state across the back-edge.</summary>
    private static Struct Append(string field)
    {
        var transform = new Struct();
        transform.Fields.Add(field, Value.ForString($"{{{{ input.{field} }}}}x"));
        return transform;
    }

    /// <summary>Condition that stays true until the marker has accumulated <paramref name="laps"/> characters.</summary>
    private static string NotYetAt(int laps) => $"input.marker !== \"{MarkerAfter(laps)}\"";

    private void StubJson(string path) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));

    private int CallCount(string path) =>
        fixture.WireMockServer.LogEntries.Count(e => e.RequestMessage.Path == path);

    private async Task<GetExecutionResponse> WaitForStatus(
        string executionId, ExecutionStatus status, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (true)
        {
            var execution = await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = executionId });

            if (execution.Summary.Status == status)
                return execution;

            if (cts.IsCancellationRequested)
                throw new TimeoutException(
                    $"Execution {executionId} was {execution.Summary.Status}, not {status}, " +
                    $"within {timeout}");

            await Task.Delay(200, cts.Token);
        }
    }

    private async Task WaitForLaps(string path, int laps, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (CallCount(path) < laps)
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException(
                    $"Only {CallCount(path)} of {laps} laps ran within {timeout}");

            await Task.Delay(200, cts.Token);
        }
    }

    private List<string> MarkersSent(string path) =>
        fixture.WireMockServer.LogEntries
            .Where(e => e.RequestMessage.Path == path)
            .Select(e => e.RequestMessage.Headers!
                .First(h => h.Key == "X-Marker").Value.First())
            .ToList();

    private async Task AssertCompleted(string executionId)
    {
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = executionId });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }

    private async Task<List<WatchExecutionResponse>> WatchToCompletion(string executionId)
    {
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });

        return await call.CollectUntilCompletion(TimeSpan.FromSeconds(60));
    }
}
