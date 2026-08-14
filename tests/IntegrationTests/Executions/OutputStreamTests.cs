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
/// An execution's output stream end to end: the <c>emit</c> action, the durable entries it
/// appends, and <c>WatchOutput</c> reading them back by cursor.
///
/// The workflow under test is the motivating shape — a poller that yields a value per lap and
/// then returns — so the stream it produces is N value entries followed by exactly one terminal
/// entry, which is what makes ordering between emissions and the final result arithmetic rather
/// than a rule.
///
/// Reads here are unprivileged: a <c>WatchOutput</c> client observes and never gates the
/// producer. Gating belongs to a <c>workflow.call</c> parent that declared <c>on_emitted</c>,
/// which does not exist yet.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class OutputStreamTests(DaemonTestFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Emits <paramref name="laps"/> values numbered from 1, then returns. The counter rides the
    /// back-edge's input transform, so each lap observes the previous lap's value.
    /// </summary>
    private TestWorkflowRequest Poller(string name, string path, int laps)
    {
        StubJson(path);

        return new TestWorkflowBuilder(name, fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .OnEmit(
                Fields(("n", Value.ForString("{{ input.n }}"))),
                "poll",
                Fields(("n", Value.ForString("{{ input.n + 1 }}"))),
                when: $"{{{{ input.n <= {laps} }}}}")
            .OnResult(Fields(("done", Value.ForBool(true))))
            .WithInput("n", 1)
            .Build();
    }

    [Fact]
    public async Task Emit_AppendsEachValueInOrder_ThenTheTerminalResult()
    {
        var scheduled = await fixture.ScheduleAsync(Poller("emit-three", "/emit-three", 3));
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var entries = await ReadOutput(scheduled.Id);

        Assert.Equal(4, entries.Count);

        // Dense and monotonic from 0 — the sequence is the cursor, so a gap would break resuming.
        Assert.Equal([0L, 1L, 2L, 3L], entries.Select(entry => entry.Sequence));

        Assert.Equal([1d, 2d, 3d], entries.Take(3).Select(entry => entry.Value.Fields["n"].NumberValue));

        // Exactly one terminal entry, always last.
        var terminal = entries[^1];
        Assert.Equal(WatchOutputResponse.EntryOneofCase.Result, terminal.EntryCase);
        Assert.True(terminal.Result.Fields["done"].BoolValue);
        Assert.DoesNotContain(entries.Take(3), entry => entry.EntryCase != WatchOutputResponse.EntryOneofCase.Value);

        Assert.All(entries, entry => Assert.NotNull(entry.Timestamp));
    }

    [Fact]
    public async Task WatchOutput_ResumesAfterACursor_WithNoGapAndNoRepeat()
    {
        var scheduled = await fixture.ScheduleAsync(Poller("emit-cursor", "/emit-cursor", 3));
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        // The whole point of a cursor: a reader that stopped at entry 1 asks for what follows and
        // gets exactly that, without re-reading what it already processed.
        var resumed = await ReadOutput(scheduled.Id, after: 1);

        Assert.Equal([2L, 3L], resumed.Select(entry => entry.Sequence));
        Assert.Equal(3d, resumed[0].Value.Fields["n"].NumberValue);
        Assert.Equal(WatchOutputResponse.EntryOneofCase.Result, resumed[^1].EntryCase);
    }

    [Fact]
    public async Task WatchOutput_ReadsTheLastEntries_WhenGivenATail()
    {
        var scheduled = await fixture.ScheduleAsync(Poller("emit-tail", "/emit-tail", 3));
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var tailed = await ReadOutput(scheduled.Id, tail: 2);

        Assert.Equal([2L, 3L], tailed.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task WatchOutput_ReplaysAFinishedExecutionAndThenEnds()
    {
        var scheduled = await fixture.ScheduleAsync(Poller("emit-replay", "/emit-replay", 2));
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        // Twice, from the same position: replay re-reads recorded entries rather than re-running
        // anything, so the second read is identical to the first.
        var first = await ReadOutput(scheduled.Id);
        var second = await ReadOutput(scheduled.Id);

        Assert.Equal(3, first.Count);
        Assert.Equal(
            first.Select(entry => entry.Sequence),
            second.Select(entry => entry.Sequence));

        // The stub was called once per lap plus the pass that exits, and not again for the reads.
        Assert.Equal(3, CallCount("/emit-replay"));
    }

    [Fact]
    public async Task WatchOutput_EndsImmediately_WhenTheCursorIsAlreadyPastTheTerminalEntry()
    {
        // A reader resuming from its last cursor on a finished run has nothing left to wait for.
        // Left to subscribe, it would hang on a stream that can never produce another entry.
        var scheduled = await fixture.ScheduleAsync(Poller("emit-caught-up", "/emit-caught-up", 1));
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var all = await ReadOutput(scheduled.Id);
        var beyond = await ReadOutput(scheduled.Id, after: all[^1].Sequence);

        Assert.Empty(beyond);
    }

    [Fact]
    public async Task WatchOutput_FollowsALiveExecution_AndEndsWhenItTerminates()
    {
        const string path = "/emit-live";
        StubJson(path);

        // A timer between laps, so the read starts while the run is still producing.
        var request = new TestWorkflowBuilder("emit-live", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .OnEmit(
                Fields(("n", Value.ForString("{{ input.n }}"))),
                "wait",
                Fields(("n", Value.ForString("{{ input.n + 1 }}"))),
                when: "{{ input.n <= 3 }}")
            .OnResult(Fields(("done", Value.ForBool(true))))
            .Timer("wait", TimeSpan.FromSeconds(1))
            .OnSuccess("poll")
            .WithInput("n", 1)
            .Build("poll");

        var scheduled = await fixture.ScheduleAsync(request);

        // No completion wait first: the server closes the stream on the terminal entry, so the
        // read ending at all is the assertion. Should the run finish before this starts, it
        // replays and ends the same way.
        var entries = await ReadOutput(scheduled.Id);

        Assert.Equal(4, entries.Count);
        Assert.Equal([1d, 2d, 3d], entries.Take(3).Select(entry => entry.Value.Fields["n"].NumberValue));
        Assert.Equal(WatchOutputResponse.EntryOneofCase.Result, entries[^1].EntryCase);
    }

    [Fact]
    public async Task AFailedRun_TerminatesItsStreamWithAnError()
    {
        const string path = "/emit-then-fail";
        StubJson(path);

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/emit-boom").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("emit-then-fail", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .OnEmit(Fields(("stage", Value.ForString("polled"))), "boom")
            .HttpGet("boom", "/emit-boom")
            .Build("poll");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var entries = await ReadOutput(scheduled.Id);

        // The value it managed to produce is still there — the failure terminates the stream, it
        // does not retract what was already emitted.
        Assert.Equal(2, entries.Count);
        Assert.Equal("polled", entries[0].Value.Fields["stage"].StringValue);

        Assert.Equal(WatchOutputResponse.EntryOneofCase.Error, entries[^1].EntryCase);
        Assert.Equal("EXECUTION_FAILED", entries[^1].Error.Code);
        Assert.NotEmpty(entries[^1].Error.Message);
    }

    [Fact]
    public async Task ARunThatEndsWithoutReturning_StillTerminatesItsStream()
    {
        const string path = "/emit-no-result";
        StubJson(path);

        var request = new TestWorkflowBuilder("emit-no-result", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .Build();

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var entries = await ReadOutput(scheduled.Id);

        // An empty structure rather than a missing entry: the stream always terminates, and a
        // client should not have to tell "returned nothing" from "nothing recorded".
        var terminal = Assert.Single(entries);
        Assert.Equal(WatchOutputResponse.EntryOneofCase.Result, terminal.EntryCase);
        Assert.Empty(terminal.Result.Fields);
    }

    [Fact(Skip = SkipReasons.DaprDropsTerminationsSharingAWorkItem)]
    public async Task ACancelledRun_TerminatesItsStreamWithACancellationError()
    {
        var request = new TestWorkflowBuilder("emit-cancelled", fixture.MockBaseUrl)
            .Timer("wait", TimeSpan.FromSeconds(1))
            .OnSuccess("wait")          // back-edge with no exit: runs until cancelled
            .Build();

        var scheduled = await fixture.ScheduleAsync(request);
        await WaitForStatus(scheduled.Id, ExecutionStatus.Active);

        await fixture.DaemonClient.CancelExecutionAsync(
            new CancelExecutionRequest { ExecutionId = scheduled.Id, Reason = "no longer needed" });

        var entries = await ReadOutput(scheduled.Id);

        // Cancellation tears the orchestration down, so nothing else would ever close this
        // stream — a reader would wait on an entry that is never coming.
        var terminal = Assert.Single(entries);
        Assert.Equal(WatchOutputResponse.EntryOneofCase.Error, terminal.EntryCase);
        Assert.Equal("EXECUTION_CANCELLED", terminal.Error.Code);
        Assert.Equal("no longer needed", terminal.Error.Message);
    }

    [Fact]
    public async Task WatchOutput_ThrowsNotFound_WhenTheExecutionDoesNotExist()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(
            () => ReadOutput(Guid.NewGuid().ToString()));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task WatchOutput_ThrowsInvalidArgument_WhenTheIdIsMalformed()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(
            () => ReadOutput("not-a-valid-guid"));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private async Task<List<WatchOutputResponse>> ReadOutput(
        string executionId, long? after = null, long? tail = null)
    {
        var request = new WatchOutputRequest { ExecutionId = executionId };

        if (after.HasValue)
            request.After = after.Value;
        else if (tail.HasValue)
            request.Tail = tail.Value;

        using var call = fixture.DaemonClient.WatchOutput(request);
        using var cts = new CancellationTokenSource(Timeout);

        var entries = new List<WatchOutputResponse>();

        while (await call.ResponseStream.MoveNext(cts.Token))
            entries.Add(call.ResponseStream.Current);

        return entries;
    }

    private static Struct Fields(params (string Key, Value Value)[] fields)
    {
        var result = new Struct();

        foreach (var (key, value) in fields)
            result.Fields.Add(key, value);

        return result;
    }

    private void StubJson(string path) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));

    private int CallCount(string path) =>
        fixture.WireMockServer.LogEntries.Count(entry => entry.RequestMessage.Path == path);

    private async Task WaitForStatus(string executionId, ExecutionStatus status)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var execution = await fixture.DaemonClient.GetExecutionAsync(
                    new GetExecutionRequest { ExecutionId = executionId });

                if (execution.Summary.Status == status)
                    return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // The read model has not caught up with the schedule yet.
            }

            await Task.Delay(200, cts.Token);
        }

        throw new TimeoutException($"Execution {executionId} never reached {status}");
    }
}
