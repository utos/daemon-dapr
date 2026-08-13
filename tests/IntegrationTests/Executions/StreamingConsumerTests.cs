using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using Utos.Workflows.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// The motivating case: a sub-workflow that owns its own loop and yields values as it goes, and a
/// caller that handles each one in order without knowing how the loop works.
///
/// The caller declares <c>on_emitted</c>, which makes it the child's privileged consumer. Every
/// value is a rendezvous — the child appends its entry, hands the value over, and parks until the
/// caller says it has been consumed — so the unconsumed buffer never exceeds one entry however
/// much faster the producer is.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class StreamingConsumerTests(DaemonTestFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>Emits 1..<paramref name="values"/>, then returns how many it produced.</summary>
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

    /// <summary>The handler path a consumed value takes, and back to the call activity.</summary>
    private static TransitionRule[] HandleThenResume(string handlerActivity) =>
        [new TransitionRule { Transition = new TransitionTarget { Name = handlerActivity } }];

    [Fact]
    public async Task Each_emitted_value_reaches_the_callers_handler_in_order()
    {
        const string producePath = "/streaming-produce";
        const string handlePath = "/streaming-handle";
        StubJson(producePath);
        StubJson(handlePath);

        var request = new TestWorkflowBuilder("streaming-in-order", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: HandleThenResume("handle"))
            .OnResult(Fields(("produced", Value.ForString("{{ output.produced }}"))))
            // The handler marks each value, then transitions back to the call activity — which is
            // what both acknowledges the value and asks for the next.
            .HttpGet("handle", handlePath)
            .WithHeader("X-Value", "{{ input.n }}")
            .OnSuccess("watch")
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 3))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // Every value, exactly once, in the order produced.
        Assert.Equal(["1", "2", "3"], ValuesHandled(handlePath));

        // And then the terminal value through on_success, not through the handler.
        Assert.NotNull(details.Result);
        Assert.Equal(3d, details.Result.Fields["produced"].NumberValue);
    }

    [Fact]
    public async Task The_producer_never_runs_ahead_of_the_consumer()
    {
        // Backpressure, observed rather than asserted about. The producer does no work per lap
        // while the consumer takes a second over each value, so an ungated producer would finish
        // all its laps long before the first handler call. Interleaving is the evidence that the
        // child is parked on each acknowledgement.
        const string producePath = "/streaming-paced-produce";
        const string handlePath = "/streaming-paced-handle";
        StubJson(producePath);
        StubJson(handlePath);

        var request = new TestWorkflowBuilder("streaming-paced", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: HandleThenResume("slow"))
            .OnResult(Fields(("done", Value.ForBool(true))))
            .Timer("slow", TimeSpan.FromSeconds(1))
            .OnSuccess("handle")
            .HttpGet("handle", handlePath)
            .WithHeader("X-Value", "{{ input.n }}")
            .OnSuccess("watch")
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 3))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        Assert.Equal(["1", "2", "3"], ValuesHandled(handlePath));

        // The producer's laps and the consumer's handling alternate. Every produce call bar the
        // last is followed by a handle call before the next produce — which cannot happen unless
        // the producer waited.
        var interleaved = fixture.WireMockServer.LogEntries
            .Where(entry => entry.RequestMessage.Path == producePath
                            || entry.RequestMessage.Path == handlePath)
            .OrderBy(entry => entry.RequestMessage.DateTime)
            .Select(entry => entry.RequestMessage.Path == producePath ? "produce" : "handle")
            .ToList();

        Assert.Equal(
            ["produce", "handle", "produce", "handle", "produce", "handle", "produce"],
            interleaved);
    }

    [Fact]
    public async Task A_producer_that_fails_mid_stream_surfaces_to_its_caller()
    {
        // The values already handed over stand; the failure arrives as the terminal message and
        // fails the caller, rather than leaving it waiting for a fourth value that is not coming.
        const string producePath = "/streaming-fail-produce";
        const string handlePath = "/streaming-fail-handle";
        const string boomPath = "/streaming-fail-boom";
        StubJson(producePath);
        StubJson(handlePath);

        fixture.WireMockServer
            .Given(Request.Create().WithPath(boomPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("streaming-failure", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: HandleThenResume("handle"))
            .HttpGet("handle", handlePath)
            .WithHeader("X-Value", "{{ input.n }}")
            .OnSuccess("watch")
            .AddDependentWorkflow("producer-workflow", child =>
                child.HttpGet("produce", producePath)
                    .OnEmit(
                        Fields(("n", Value.ForString("{{ input.n }}"))),
                        "produce",
                        Fields(("n", Value.ForString("{{ input.n + 1 }}"))),
                        when: "{{ input.n <= 2 }}")
                    .OnSuccess("boom")
                    .HttpGet("boom", boomPath))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Failed, details.Summary.Status);
        Assert.Equal(["1", "2"], ValuesHandled(handlePath));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private List<string> ValuesHandled(string path) =>
        fixture.WireMockServer.LogEntries
            .Where(entry => entry.RequestMessage.Path == path)
            .OrderBy(entry => entry.RequestMessage.DateTime)
            .Select(entry => entry.RequestMessage.Headers!
                .First(header => header.Key == "X-Value").Value.First())
            .ToList();

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
}
