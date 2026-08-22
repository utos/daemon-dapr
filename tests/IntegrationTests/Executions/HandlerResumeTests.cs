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
/// Consuming a sub-workflow's output without writing a back-edge.
///
/// A handler used to have to transition back to the call activity by name, and forgetting was
/// close to undetectable: the subscription ended, the producer was cancelled, and the run reported
/// Completed after one value. Now a handler that runs out of rules has simply finished, and control
/// returns to the call activity for the next value — the way a promise branch returns to its
/// promise.
///
/// <see cref="StreamingConsumerTests"/> covers the same ground with the explicit back-edge still in
/// place, which is what keeps both forms honest.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class HandlerResumeTests(DaemonTestFixture fixture)
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

    /// <summary>
    /// Hand each value to <paramref name="startActivity"/> of the handler document.
    /// <para>
    /// A handler is always another document — `self` is rejected here (UTOS-S011) — which is what
    /// makes it structurally unable to transition back into the consumer's flow.
    /// </para>
    /// </summary>
    private static DispatchRule[] Dispatch(string startActivity, string? condition = null)
    {
        var rule = new DispatchRule
        {
            Workflow = HandlerWorkflow,
            StartActivity = startActivity
        };

        if (condition != null)
            rule.Condition = condition;

        return [rule];
    }

    private const string HandlerWorkflow = "handler-workflow";

    [Fact]
    public async Task A_handler_with_no_back_edge_still_consumes_every_value()
    {
        const string producePath = "/resume-produce";
        const string handlePath = "/resume-handle";
        StubJson(producePath);
        StubJson(handlePath);

        var request = new TestWorkflowBuilder("resume-no-back-edge", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: Dispatch("handle"))
            .OnResult(Fields(("produced", Value.ForString("{{ output.produced }}"))))
            .AddDependentWorkflow(HandlerWorkflow, handler => handler
                .HttpGet("handle", handlePath)
                .WithHeader("X-Value", "{{ input.n }}"))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 3))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // Every value, exactly once, in order — the same assertion the back-edge form makes.
        Assert.Equal(["1", "2", "3"], ValuesHandled(handlePath));

        // And the run went on to see the producer's terminal value rather than stopping at one.
        Assert.NotNull(details.Result);
        Assert.Equal(3d, details.Result.Fields["produced"].NumberValue);
    }

    [Fact]
    public async Task The_end_of_a_handler_chain_is_what_returns_not_its_first_activity()
    {
        const string producePath = "/resume-chain-produce";
        const string enrichPath = "/resume-chain-enrich";
        const string storePath = "/resume-chain-store";
        StubJson(producePath);
        StubJson(enrichPath);
        StubJson(storePath);

        // The non-local coupling this removes: the back-edge used to live in `store`, the last
        // activity of the chain, which then knew it belonged to one particular call activity.
        var request = new TestWorkflowBuilder("resume-chain", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: Dispatch("enrich"))
            .OnResult(Fields(("produced", Value.ForString("{{ output.produced }}"))))
            .AddDependentWorkflow(HandlerWorkflow, handler => handler
                .HttpGet("enrich", enrichPath)
                .WithHeader("X-Value", "{{ input.n }}")
                .OnSuccess("store", Fields(("n", Value.ForString("{{ input.n }}"))))
                .HttpGet("store", storePath)
                .WithHeader("X-Value", "{{ input.n }}"))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 3))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // Both ends of the chain ran for every value, so the resume came from `store` finishing
        // rather than from `enrich` handing off.
        Assert.Equal(["1", "2", "3"], ValuesHandled(enrichPath));
        Assert.Equal(["1", "2", "3"], ValuesHandled(storePath));
    }

    [Fact]
    public async Task A_handler_rule_that_matches_nothing_skips_the_value_and_keeps_consuming()
    {
        const string producePath = "/resume-filter-produce";
        const string handlePath = "/resume-filter-handle";
        StubJson(producePath);
        StubJson(handlePath);

        // The trap the old behaviour set. A filtering consumer exhausts its rule list on every
        // value it does not want, which used to end the subscription, cancel the producer and
        // report the run Completed — after one uninteresting email.
        var request = new TestWorkflowBuilder("resume-filter", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: Dispatch("handle", condition: "{{ output.n == '2' }}"))
            .OnResult(Fields(("produced", Value.ForString("{{ output.produced }}"))))
            .AddDependentWorkflow(HandlerWorkflow, handler => handler
                .HttpGet("handle", handlePath)
                .WithHeader("X-Value", "{{ input.n }}"))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 4))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // Only the value it asked for was handled...
        Assert.Equal(["2"], ValuesHandled(handlePath));

        // ...and the producer ran to completion regardless, which is the part that used not to.
        Assert.NotNull(details.Result);
        Assert.Equal(4d, details.Result.Fields["produced"].NumberValue);
    }

    [Fact]
    public async Task A_failing_handler_fails_the_run_rather_than_taking_the_next_value()
    {
        const string producePath = "/resume-fail-produce";
        const string handlePath = "/resume-fail-handle";
        StubJson(producePath);

        // Where resuming and propagating meet. Going back for the next value here would swallow
        // the failure and leave the run looking healthy.
        fixture.WireMockServer
            .Given(Request.Create().WithPath(handlePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("resume-fail", fixture.MockBaseUrl)
            .WorkflowCall("watch", "producer-workflow", "produce",
                input: Fields(("n", Value.ForNumber(1))),
                onEmitted: Dispatch("handle"))
            .OnResult(Fields(("produced", Value.ForString("{{ output.produced }}"))))
            .AddDependentWorkflow(HandlerWorkflow, handler => handler
                .HttpGet("handle", handlePath)
                .WithHeader("X-Value", "{{ input.n }}"))
            .AddDependentWorkflow("producer-workflow", child => Producer(child, producePath, 3))
            .Build("watch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Failed, details.Summary.Status);

        // It stopped at the first value rather than working through the rest.
        Assert.Equal(1, CallCount(handlePath));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

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
