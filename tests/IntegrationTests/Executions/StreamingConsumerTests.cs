using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
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
///
/// Every handler here transitions back to the call activity by name, and that is deliberate: it is
/// no longer required — a handler that runs out of rules goes back on its own, which
/// <see cref="HandlerResumeTests"/> covers — so this suite doubles as the guarantee that the
/// explicit form still means exactly what it always did.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class StreamingConsumerTests(DaemonTestFixture fixture)
{
    // Raised from 90s: a handler is a child execution now, so each consumed value costs a
    // context save, a child start, a rendezvous and a stream delete on top of the handler work.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(240);

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
    private static DispatchRule[] HandleThenResume(string handlerActivity) =>
    [
        new DispatchRule
        {
            Workflow = TestWorkflowBuilder.SelfReference,
            StartActivity = handlerActivity
        }
    ];

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
    public async Task A_subscription_survives_the_consumers_continuation()
    {
        // The highest-risk case in the design. A consumer re-enters its call activity once per
        // value, so a stream of any length crosses ContinueAsNew — every five transitions under
        // this fixture, so roughly every other value. WorkflowExecutorInput *is* the continuation
        // envelope, and the subscription (which activity is subscribed, and to which child) rides
        // in it: drop it and the next segment does not fail, it starts a second child and consumes
        // from the beginning again. So the assertion that matters is not "it completed" but
        // "every value arrived exactly once, in order" — a rewind shows up as a repeat, a reset as
        // a duplicate child re-polling from 1.
        //
        // Verified by breaking it: emptying the subscription carry in ContinueFrom makes this fail
        // on the completion wait rather than on an assertion, because a caller that forgets its
        // subscription starts a fresh child at every continuation and the run never ends at all.
        // A timeout here is the expected shape of that failure, not a flaky test.
        const string producePath = "/streaming-continued-produce";
        const string handlePath = "/streaming-continued-handle";
        StubJson(producePath);
        StubJson(handlePath);

        const int values = 8;

        var request = new TestWorkflowBuilder("streaming-continued", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: HandleThenResume("handle"))
            .OnResult(Fields(("produced", Value.ForString("{{ output.produced }}"))))
            .HttpGet("handle", handlePath)
            .WithHeader("X-Value", "{{ input.n }}")
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, values))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        var events = await WatchToCompletion(scheduled.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // The run really did span several Dapr instances rather than fitting in one, which is what
        // makes the rest of this test mean anything.
        Assert.Contains(events, e => e.Message.Contains("Continuing as new"));

        // Every value, exactly once, in order — across those continuations.
        Assert.Equal(
            Enumerable.Range(1, values).Select(value => value.ToString()),
            ValuesHandled(handlePath));

        // The child produced them from one run rather than being restarted: a second child would
        // have polled its way from 1 again, so the stub would show more laps than values.
        Assert.Equal(values + 1, CallCount(producePath));

        // And the terminal value still arrives through on_success once the stream ends.
        Assert.NotNull(details.Result);
        Assert.Equal((double)values, details.Result.Fields["produced"].NumberValue);
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
    public async Task A_consumer_can_relay_what_it_consumes_to_its_own_caller()
    {
        // Three levels: a producer, a middle workflow that consumes it *and* emits to its own
        // caller, and a top-level run consuming that. Each link is its own rendezvous, so the
        // middle one is parked in both directions at once — waiting on its caller's
        // acknowledgement before it can acknowledge its child.
        //
        // Worth proving rather than assuming, because the nearest piece of machinery is
        // deliberately one level deep: log lines route to `ParentExecutionId ?? ExecutionId`, so a
        // grandchild's logs land in a buffer nobody watches. Output streams are per-execution and
        // addressed by their own id, and this is what says they did not inherit that shape.
        const string producePath = "/nested-produce";
        const string relayPath = "/nested-relay";
        const string handlePath = "/nested-handle";
        StubJson(producePath);
        StubJson(relayPath);
        StubJson(handlePath);

        const int values = 3;

        var request = new TestWorkflowBuilder("nested-top", fixture.MockBaseUrl)
            .WorkflowCall("watch-middle", "middle-workflow", "watch-child",
                onEmitted: HandleThenResume("handle"))
            .OnResult(Fields(("done", Value.ForString("{{ output.relayed }}"))))
            .HttpGet("handle", handlePath)
            .WithHeader("X-Value", "{{ input.n }}")
            .AddDependentWorkflow("middle-workflow", middle => middle
                .WorkflowCall("watch-child", "producer-workflow", "produce",
                    input: Fields(("n", Value.ForNumber(1))),
                    onEmitted: HandleThenResume("relay"))
                .OnResult(Fields(("relayed", Value.ForString("{{ output.produced }}"))))
                .HttpGet("relay", relayPath)
                .WithHeader("X-Value", "{{ input.n }}")
                // Emit-and-end: hand the value up, then finish. The handler is its own execution
                // now, so finishing is what acknowledges the producer and asks for the next value
                // — transitioning back to the call activity would open a second producer here.
                // `emit` still needs a target (UTOS-T004), and `end` is the one that means "done".
                .OnEmit(Fields(("n", Value.ForString("{{ input.n }}"))), "end"))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, values))
            .Build("watch-middle");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // Every value reached the top in order, having crossed two subscriptions to get there.
        Assert.Equal(["1", "2", "3"], ValuesHandled(handlePath));

        // And the middle relayed each one exactly once rather than batching or dropping.
        Assert.Equal(["1", "2", "3"], ValuesHandled(relayPath));

        // The terminal value travels the same chain: the child's result becomes the middle's,
        // which becomes the top's — and stays a number across both hops, since each is a whole
        // expression rather than an interpolation.
        Assert.NotNull(details.Result);
        Assert.Equal((double)values, details.Result.Fields["done"].NumberValue);
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

    [Fact]
    public async Task A_handler_that_ends_finishes_only_itself()
    {
        // A handler is a document now, so `end` ends the *handler*, not the consumer. This used to
        // be how a consumer stopped early — the handler transitioned to `end` and took the whole
        // run with it — and that is gone, deliberately: nothing a dispatched document does can
        // reach into the flow that dispatched it, which is the property the document boundary
        // exists to provide.
        //
        // It leaves a real gap, recorded here rather than hidden. `on_emitted` rules are
        // dispatches, so a consumer can no longer decide to stop based on what it consumed; it
        // runs until the producer terminates. Expressing "enough" needs a spec decision (a
        // terminal dispatch action, or a handler result the consumer reads) and is not something
        // to invent in the daemon.
        const string producePath = "/streaming-abandon-produce";
        const string handlePath = "/streaming-abandon-handle";
        StubJson(producePath);
        StubJson(handlePath);

        var request = new TestWorkflowBuilder("streaming-abandon", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: HandleThenResume("handle"))
            .HttpGet("handle", handlePath)
            .WithHeader("X-Value", "{{ input.n }}")
            .OnSuccess("end")
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 5))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await WatchToCompletion(scheduled.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // Every value handled, not one. The handler ending five times is five finished handlers,
        // and the run ends when the producer does.
        Assert.Equal(["1", "2", "3", "4", "5"], ValuesHandled(handlePath));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private async Task<List<WatchExecutionResponse>> WatchToCompletion(string executionId)
    {
        var call = fixture.ObservabilityClient.WatchExecution(
            new WatchExecutionRequest { ExecutionId = executionId });

        return await call.CollectUntilCompletion(Timeout);
    }

    private int CallCount(string path) =>
        fixture.WireMockServer.LogEntries.Count(entry => entry.RequestMessage.Path == path);

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
