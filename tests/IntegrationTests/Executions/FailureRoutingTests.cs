using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// End-to-end <c>on_failure</c> routing. <see cref="ExecutionFailureTests"/> covers the case where
/// nothing handles the failure, which is the regression these must not introduce.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class FailureRoutingTests(DaemonTestFixture fixture)
{
    private static Utos.Daemon.V1.ExecutionStatus? TerminalStatus(
        IEnumerable<WatchExecutionResponse> events)
    {
        return events.LastOrDefault(e =>
                e.Status is ExecutionStatus.Completed or ExecutionStatus.Failed)
            ?.Status;
    }

    [Fact]
    public async Task A_handled_failure_routes_onward_and_the_run_completes()
    {
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/boom").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/recovered").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"ok\": true}"));

        var request = new TestWorkflowBuilder("handled-failure", fixture.MockBaseUrl)
            .HttpGet("call", "/boom").OnFailure("recover")
            .HttpGet("recover", "/recovered")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatus.Completed, TerminalStatus(events));

        // The handler actually ran, rather than the run merely surviving.
        Assert.Single(fixture.WireMockServer.LogEntries, e => e.RequestMessage.Path == "/recovered");
    }

    [Fact]
    public async Task A_failure_rule_can_branch_on_the_response_status()
    {
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/throttled").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(429)
                .WithHeader("Retry-After", "1"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/backoff").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/reauth").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{}"));

        var request = new TestWorkflowBuilder("status-branching", fixture.MockBaseUrl)
            .HttpGet("call", "/throttled")
                .OnFailure("reauth", when: "response.status === 401")
                .OnFailure("backoff", when: "response.status === 429")
            .HttpGet("backoff", "/backoff")
            .HttpGet("reauth", "/reauth")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatus.Completed, TerminalStatus(events));

        var paths = fixture.WireMockServer.LogEntries
            .Select(e => e.RequestMessage.Path).ToList();

        Assert.Contains("/backoff", paths);
        Assert.DoesNotContain("/reauth", paths);
    }

    [Fact]
    public async Task A_failure_whose_rules_all_miss_still_fails_the_run()
    {
        // The asymmetry that makes on_failure different from on_success: rules present but none
        // matching must not be read as "nothing left to do, so we are finished".
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/server-error").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var request = new TestWorkflowBuilder("unmatched-failure", fixture.MockBaseUrl)
            .HttpGet("call", "/server-error")
                .OnFailure("never", when: "response.status === 429")
            .HttpGet("never", "/server-error")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatus.Failed, TerminalStatus(events));
    }

    [Fact]
    public async Task An_error_action_on_the_failure_path_reports_the_author_s_code_and_can_quote_the_cause()
    {
        // The 0.0.16 replacement for `transition: { name: error }` on the failure path, and what
        // that could never do: name the failure. The cause is not reported by default — the
        // action means "fail with *this*" — but `{{ error.message }}` quotes it, and
        // `{{ response.status }}` is there for the part that carries real detail.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/teapot").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(418));

        var request = new TestWorkflowBuilder("explicit-error", fixture.MockBaseUrl)
            .HttpGet("call", "/teapot")
                .OnFailure("never", when: "response.status === 429")
                .OnFailureError("TEAPOT", "upstream said {{ response.status }}: {{ error.message }}")
            .HttpGet("never", "/teapot")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatus.Failed, TerminalStatus(events));

        var terminal = events.Last(e => e.Status == ExecutionStatus.Failed);

        Assert.Equal("TEAPOT", terminal.Error.Code);
        Assert.StartsWith("upstream said 418: ", terminal.Error.Message);
        Assert.Contains("418", terminal.Error.Message[19..]);
    }

    [Fact]
    public async Task An_unhandled_failure_still_reports_the_generic_code()
    {
        // The other side of the previous test: a failure the daemon raised, with no rule
        // rewriting it, reports EXECUTION_FAILED and the cause — the pre-0.0.16 behaviour, kept.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/teapot-unhandled").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(418));

        var request = new TestWorkflowBuilder("unhandled-generic", fixture.MockBaseUrl)
            .HttpGet("call", "/teapot-unhandled")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        var terminal = events.Last(e => e.Status == ExecutionStatus.Failed);
        Assert.Equal("EXECUTION_FAILED", terminal.Error.Code);
        Assert.Contains("418", terminal.Error.Message);
    }

    [Fact]
    public async Task A_sub_workflow_s_authored_error_is_what_the_caller_s_failure_rule_sees()
    {
        // The whole reason an error has a code: the child decides to fail with ORDER_NOT_FOUND,
        // and the caller's on_failure matches on exactly that — through the runtime's exception,
        // which flattens everything to a string, by reading the child's stream back.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/order-lookup").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"found": false, "id": "o-9"}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/order-missing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/order-other").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{}"));

        // The failure rule's own scope is where `error` is defined; the handler activity runs on
        // its success path, so the rule hands the error on as the handler's input.
        var carried = new Struct();
        carried.Fields.Add("code", Value.ForString("{{ error.code }}"));
        carried.Fields.Add("message", Value.ForString("{{ error.message }}"));
        carried.Fields.Add("missingId", Value.ForString("{{ error.details.id }}"));

        var handled = new Struct();
        handled.Fields.Add("code", Value.ForString("{{ input.code }}"));
        handled.Fields.Add("message", Value.ForString("{{ input.message }}"));
        handled.Fields.Add("missingId", Value.ForString("{{ input.missingId }}"));

        var childDetails = new Struct();
        childDetails.Fields.Add("id", Value.ForString("{{ output.id }}"));

        var request = new TestWorkflowBuilder("child-error-code", fixture.MockBaseUrl)
            .WorkflowCall("lookup", "lookup-workflow", "fetch")
                .OnFailure("missing", carried, when: "error.code === 'ORDER_NOT_FOUND'")
                .OnFailure("other")
            .HttpGet("missing", "/order-missing")
                .OnResult(handled)
            .HttpGet("other", "/order-other")
            .AddDependentWorkflow("lookup-workflow", child => child
                .HttpGet("fetch", "/order-lookup")
                .OnError("ORDER_NOT_FOUND", "order {{ output.id }} does not exist", childDetails, when: "!output.found")
                .OnEnd())
            .Build("lookup");

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id, TimeSpan.FromSeconds(60));

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
        Assert.Single(fixture.WireMockServer.LogEntries, e => e.RequestMessage.Path == "/order-missing");
        Assert.Empty(fixture.WireMockServer.LogEntries.Where(e => e.RequestMessage.Path == "/order-other"));

        // The failure rule saw the child's error whole: the code, the rendered message and the
        // rendered details — not a generic code wrapped around the child's text.
        Assert.Equal("ORDER_NOT_FOUND", execution.Result.Fields["code"].StringValue);
        Assert.Equal("order o-9 does not exist", execution.Result.Fields["message"].StringValue);
        Assert.Equal("o-9", execution.Result.Fields["missingId"].StringValue);
    }

    [Fact]
    public async Task Workflow_http_is_attempted_exactly_once()
    {
        // Guards the resilience split: the workflow client must not inherit the standard retry
        // pipeline from ConfigureHttpClientDefaults, because a workflow's request is not idempotent.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/once").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var request = new TestWorkflowBuilder("no-retry", fixture.MockBaseUrl)
            .HttpPost("call", "/once", "{}")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Single(fixture.WireMockServer.LogEntries, e => e.RequestMessage.Path == "/once");
    }

    [Fact]
    public async Task An_author_declared_content_type_reaches_the_server()
    {
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/form").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{}"));

        var request = new TestWorkflowBuilder("form-post", fixture.MockBaseUrl)
            .HttpPost("call", "/form", "grant_type=refresh_token")
                .WithHeader("content-type", "application/x-www-form-urlencoded")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        var received = Assert.Single(fixture.WireMockServer.LogEntries, e => e.RequestMessage.Path == "/form");

        Assert.Contains("application/x-www-form-urlencoded",
            received.RequestMessage.Headers!["Content-Type"].ToString());
    }
}
