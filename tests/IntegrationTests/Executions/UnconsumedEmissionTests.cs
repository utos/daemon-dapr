using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using Utos.Workflows.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// A child that emits while nobody is consuming.
///
/// <c>emit</c> is a property of the workflow that produces values, not of whoever happens to
/// invoke it, so the same sub-workflow can be called by a caller that streams its values and by
/// one that wants only the final answer. The second caller declares no <c>on_emitted</c>, and the
/// spec is explicit about what the child does then: "With no privileged consumer the emit
/// completes as soon as the entry is durable."
///
/// That makes this the case gating can break. A child that waits for an acknowledgement nobody is
/// there to send does not fail — it hangs, holding the caller with it, and the workflow that
/// worked yesterday is the one that stops. These pin the contract from both ends so the gated
/// path cannot be built at its expense.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class UnconsumedEmissionTests(DaemonTestFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A sub-workflow that emits <paramref name="values"/> values and then returns a summary.
    /// The counter rides the back-edge, so each lap sees the previous lap's value.
    /// </summary>
    private static void Producer(TestWorkflowBuilder child, string path, int values)
    {
        child.HttpGet("produce", path)
            .OnEmit(
                Fields(("n", Value.ForString("{{ input.n }}"))),
                "produce",
                Fields(("n", Value.ForString("{{ input.n + 1 }}"))),
                when: $"{{{{ input.n <= {values} }}}}")
            .OnResult(Fields(("produced", Value.ForNumber(values))));
    }

    [Fact]
    public async Task A_caller_that_declares_no_handler_still_gets_the_terminal_value()
    {
        const string path = "/unconsumed-terminal";
        StubJson(path);

        var request = new TestWorkflowBuilder("unconsumed-terminal", fixture.MockBaseUrl)
            .WorkflowCall("call", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))))
            .OnResult(Fields(("sawProduced", Value.ForString("{{ output.produced }}"))))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, path, 3))
            .Build("call");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        // Completed at all is half the assertion: an unacknowledged emit would leave this run
        // waiting on the child forever, and the failure would look like a timeout, not a bug.
        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // The activity's output is the child's *result*, never one of the values it emitted
        // along the way — those were addressed to a consumer this caller did not declare.
        Assert.NotNull(details.Result);
        Assert.Equal(3d, details.Result.Fields["sawProduced"].NumberValue);
    }

    [Fact]
    public async Task The_child_runs_to_completion_rather_than_stalling_on_its_first_emit()
    {
        // Distinguishes "the parent got a result" from "the child actually finished its work":
        // a child that stalled on emit 1 could still, in a broken design, look fine from the
        // parent if the terminal value arrived some other way. The stub call count cannot lie.
        const string path = "/unconsumed-laps";
        StubJson(path);

        var request = new TestWorkflowBuilder("unconsumed-laps", fixture.MockBaseUrl)
            .WorkflowCall("call", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, path, 3))
            .Build("call");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        // Three emitting laps plus the pass that returns.
        Assert.Equal(4, CallCount(path));
    }

    [Fact]
    public async Task A_spawned_child_emits_without_waiting_for_anyone()
    {
        // Spawn has no caller waiting on it by construction, so there is nobody who could ever
        // acknowledge. Its stream is readable, unlike an awaited child's, so this asserts the
        // values themselves rather than inferring them.
        const string path = "/unconsumed-spawned";
        StubJson(path);

        var request = new TestWorkflowBuilder("unconsumed-spawned", fixture.MockBaseUrl)
            .WorkflowSpawn("spawn", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))))
            .OnResult(Fields(("child", Value.ForString("{{ output.execution_id }}"))))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, path, 2))
            .Build("spawn");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var parent = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        var childId = parent.Result.Fields["child"].StringValue;

        var entries = await ReadOutput(childId);

        Assert.Equal([1d, 2d], entries.Take(2).Select(entry => entry.Value.Fields["n"].NumberValue));
        Assert.Equal(WatchOutputResponse.EntryOneofCase.Result, entries[^1].EntryCase);
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private async Task<List<WatchOutputResponse>> ReadOutput(string executionId)
    {
        using var call = fixture.DaemonClient.WatchOutput(
            new WatchOutputRequest { ExecutionId = executionId });
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
}
