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
/// A producer that fails part-way through its stream. `execution-output-stream.md` says an `error`
/// entry dispatches to the consumer's `on_failure`; until on_failure was executed there was nowhere
/// for it to go, so a failing producer simply killed whoever was listening.
///
/// This is deliberately end-to-end rather than a unit test of the interpreter: the routing works by
/// the error travelling out of `TakeChildOutputAsync`, through `PerformAsync` and into the driver's
/// catch, and that path is the part that was reasoned about rather than proven.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class StreamingFailureTests(DaemonTestFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private static TransitionRule[] HandleThenResume(string handlerActivity) =>
        [new TransitionRule { Transition = new TransitionTarget { Name = handlerActivity } }];

    [Fact]
    public async Task A_producer_that_fails_mid_stream_reaches_the_consumers_on_failure()
    {
        const string emitPath = "/stream-fail-emit";
        const string boomPath = "/stream-fail-boom";
        const string handlePath = "/stream-fail-handle";
        const string recoverPath = "/stream-fail-recover";

        StubJson(emitPath);
        StubJson(handlePath);
        StubJson(recoverPath);

        // The producer yields exactly one value, then walks into a 500 with no rules of its own —
        // so it fails while the consumer is still subscribed.
        fixture.WireMockServer
            .Given(Request.Create().WithPath(boomPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("streaming-failure", fixture.MockBaseUrl)
            .WorkflowCall("watch", "failing-producer", "produce",
                onEmitted: HandleThenResume("handle"))
            // Reached only if the child's terminal entry is a result rather than an error.
            .OnResult(Fields(("outcome", Value.ForString("completed"))))
            .OnFailure("recover")
            .HttpGet("handle", handlePath)
            .OnSuccess("watch")
            .HttpGet("recover", recoverPath)
            .OnResult(Fields(("outcome", Value.ForString("recovered"))))
            .AddDependentWorkflow("failing-producer", child =>
                child.HttpGet("produce", emitPath)
                    .OnEmit(Fields(("n", Value.ForNumber(1))), "boom")
                    .HttpGet("boom", boomPath))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        // The consumer survived its producer and took the failure branch, rather than dying with it.
        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        var paths = fixture.WireMockServer.LogEntries
            .Select(entry => entry.RequestMessage.Path)
            .ToList();

        // The value that was emitted before the failure was still delivered and handled.
        Assert.Contains(handlePath, paths);
        Assert.Contains(recoverPath, paths);

        // The producer stopped at its failure rather than looping.
        Assert.Single(paths.Where(path => path == boomPath));
    }

    [Fact]
    public async Task A_consumer_without_on_failure_still_dies_with_its_producer()
    {
        // The other half of the asymmetry: nothing handles the error, so it keeps travelling.
        // Without this, a bug that swallowed unhandled stream errors would look like a pass above.
        const string emitPath = "/stream-unhandled-emit";
        const string boomPath = "/stream-unhandled-boom";
        const string handlePath = "/stream-unhandled-handle";

        StubJson(emitPath);
        StubJson(handlePath);

        fixture.WireMockServer
            .Given(Request.Create().WithPath(boomPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("streaming-failure-unhandled", fixture.MockBaseUrl)
            .WorkflowCall("watch", "failing-producer-2", "produce",
                onEmitted: HandleThenResume("handle"))
            .OnResult(Fields(("outcome", Value.ForString("completed"))))
            .HttpGet("handle", handlePath)
            .OnSuccess("watch")
            .AddDependentWorkflow("failing-producer-2", child =>
                child.HttpGet("produce", emitPath)
                    .OnEmit(Fields(("n", Value.ForNumber(1))), "boom")
                    .HttpGet("boom", boomPath))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Failed, details.Summary.Status);
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
}
