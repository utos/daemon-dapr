using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for timer activities — a durable delay created on the workflow context rather than
/// inside an activity, so it survives a daemon restart and replays without re-waiting.
///
/// A timer performs no work: its input passes through unchanged as its output, which is what
/// lets a back-edge keep threading accumulating state across the wait. Combined with a loop
/// this is the polling workflow the spec describes as poll -> wait -> poll.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class TimerTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task Timer_ShouldActuallyDelayTheNextActivity()
    {
        // Arrange - two calls separated by a one second wait
        const string before = "/timer-before";
        const string after = "/timer-after";
        StubJson(before);
        StubJson(after);

        var delay = TimeSpan.FromSeconds(1);
        var request = new TestWorkflowBuilder("timer-delay-workflow", fixture.MockBaseUrl)
            .HttpGet("first", before)
            .OnSuccess("wait")
            .Timer("wait", delay)
            .OnSuccess("second")
            .HttpGet("second", after)
            .Build("first");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);
        await AssertCompleted(response.Id);

        // Assert - a delay must never resume early, so this is a hard lower bound rather than
        // a tolerance. The gap can exceed the delay (activity overhead sits either side, and
        // the deadline is rounded up to a whole second) but must never fall short of it.
        var gap = TimeOf(after) - TimeOf(before);
        Assert.True(gap >= delay,
            $"A {delay.TotalMilliseconds}ms timer must not resume early, but the activities were "
            + $"only {gap.TotalMilliseconds}ms apart");
    }

    [Fact]
    public async Task Timer_ShouldPassItsInputThroughAsOutput()
    {
        // Arrange - the transition after the timer reads {{ output.* }}. If the timer nulled
        // its output the condition would not match and the fallback branch would run.
        const string start = "/timer-passthrough";
        const string matched = "/timer-passthrough-matched";
        const string fallback = "/timer-passthrough-fallback";
        StubJson(start);
        StubJson(matched);
        StubJson(fallback);

        var carry = new Struct();
        carry.Fields.Add("token", Value.ForString("carried"));

        var request = new TestWorkflowBuilder("timer-passthrough-workflow", fixture.MockBaseUrl)
            .HttpGet("start", start)
            .OnSuccess("wait", carry)
            .Timer("wait", TimeSpan.FromMilliseconds(250))
            .OnSuccess("matched", when: "output.token === \"carried\"")
            .OnSuccess("fallback")
            .HttpGet("matched", matched)
            .HttpGet("fallback", fallback)
            .Build("start");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert
        await AssertCompleted(response.Id);
        Assert.Equal(1, CallCount(matched));
        Assert.Equal(0, CallCount(fallback));
    }

    [Fact]
    public async Task Timer_AsStartActivity_PassesWorkflowInputThrough()
    {
        // Arrange - with a timer first there is no preceding activity, so its input can only
        // come from the workflow input fallback. This is where the pass-through could
        // regress to null.
        const string path = "/timer-start";
        StubJson(path);

        var request = new TestWorkflowBuilder("timer-start-workflow", fixture.MockBaseUrl)
            .Timer("wait", TimeSpan.FromMilliseconds(250))
            .OnSuccess("call", when: "output.seed === \"from-input\"")
            .HttpGet("call", path)
            .WithHeader("X-Seed", "{{ input.seed }}")
            .WithInput("seed", "from-input")
            .Build("wait");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert - the condition matched and the value survived into the next activity
        await AssertCompleted(response.Id);
        Assert.Contains("from-input", HeadersSent(path, "X-Seed"));
    }

    [Fact]
    public async Task Timer_InAPollingLoop_PacesTheLoopAndCompletes()
    {
        // Arrange - the canonical spec example: poll -> wait -> poll, exiting on data.
        // With the fixture's low continue-as-new threshold this also spans an instance
        // boundary, so the timer has to survive continuation.
        const string path = "/timer-poll";
        StubJson(path);

        const int laps = 3;
        var target = "s" + new string('x', laps);

        var advance = new Struct();
        advance.Fields.Add("marker", Value.ForString("{{ input.marker }}x"));

        var carry = new Struct();
        carry.Fields.Add("marker", Value.ForString("{{ input.marker }}"));

        var delay = TimeSpan.FromMilliseconds(400);
        var request = new TestWorkflowBuilder("timer-poll-workflow", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .WithHeader("X-Marker", "{{ input.marker }}")
            .OnSuccess("wait", carry, when: $"input.marker !== \"{target}\"")
            .OnSuccess("end")
            .Timer("wait", delay)
            .OnSuccess("poll", advance)
            .WithInput("marker", "s")
            .Build("poll");

        // Act
        var response = await fixture.ScheduleAsync(request);
        var events = await WatchToCompletion(response.Id);

        // Assert - one poll per lap plus the final pass that exits
        await AssertCompleted(response.Id);
        Assert.Equal(laps + 1, CallCount(path));

        // Each lap waits at least the full delay, so the polls must span at least laps x delay.
        // Measured between the first and last poll rather than around the whole run, so
        // scheduling and streaming overhead cannot satisfy this on their own.
        var pollTimes = TimesOf(path);
        var acrossLoop = pollTimes.Last() - pollTimes.First();
        var expected = TimeSpan.FromMilliseconds(laps * delay.TotalMilliseconds);
        Assert.True(acrossLoop >= expected,
            $"{laps} laps of a {delay.TotalMilliseconds}ms timer must span at least "
            + $"{expected.TotalMilliseconds}ms, but the polls spanned only {acrossLoop.TotalMilliseconds}ms");

        Assert.Contains(events, e => e.Message.Contains("Loop detected at activity"));
    }

    [Fact]
    public async Task Timer_WithoutDuration_IsRejectedAtLoad()
    {
        var request = new TestWorkflowBuilder("timer-no-duration-workflow", fixture.MockBaseUrl)
            .RawTimer("wait", null)
            .Build();

        var exception = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-C201", exception.Status.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Timer_WithNonPositiveDuration_IsRejectedAtLoad(int seconds)
    {
        var request = new TestWorkflowBuilder($"timer-bad-duration-{Math.Abs(seconds)}-workflow", fixture.MockBaseUrl)
            .RawTimer("wait", Duration.FromTimeSpan(TimeSpan.FromSeconds(seconds)))
            .Build();

        var exception = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-C202", exception.Status.Detail);
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private void StubJson(string path) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));

    private int CallCount(string path) =>
        fixture.WireMockServer.LogEntries.Count(e => e.RequestMessage.Path == path);

    private DateTime TimeOf(string path) => TimesOf(path).First();

    private List<DateTime> TimesOf(string path) =>
        fixture.WireMockServer.LogEntries
            .Where(e => e.RequestMessage.Path == path)
            .Select(e => e.RequestMessage.DateTime)
            .OrderBy(t => t)
            .ToList();

    private List<string> HeadersSent(string path, string header) =>
        fixture.WireMockServer.LogEntries
            .Where(e => e.RequestMessage.Path == path)
            .SelectMany(e => e.RequestMessage.Headers!
                .Where(h => h.Key == header)
                .Select(h => h.Value.First()))
            .ToList();

    private async Task AssertCompleted(string executionId)
    {
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = executionId });

        Assert.True(execution.Summary.Status == ExecutionStatus.Completed,
            $"Expected Completed but was {execution.Summary.Status}: {execution.Summary.Error?.Message}");
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
