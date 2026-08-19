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
                .OnFailure("reauth", when: "{{ response.status == 401 }}")
                .OnFailure("backoff", when: "{{ response.status == 429 }}")
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
                .OnFailure("never", when: "{{ response.status == 429 }}")
            .HttpGet("never", "/server-error")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatus.Failed, TerminalStatus(events));
    }

    [Fact]
    public async Task Routing_a_failure_to_error_reports_the_original_cause()
    {
        // `transition: { name: error }` as a failure fallback is redundant with declaring no rule
        // — both fail the run — but an author will write it, and it must not be the worse of the
        // two. Letting it reach the navigator would replace the real diagnostic with the generic
        // "Workflow transitioned to 'error'", so adopting on_failure would make failures *harder*
        // to diagnose than leaving it out.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/teapot").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(418));

        var request = new TestWorkflowBuilder("explicit-error", fixture.MockBaseUrl)
            .HttpGet("call", "/teapot")
                .OnFailure("never", when: "{{ response.status == 429 }}")
                .OnFailure("error")
            .HttpGet("never", "/teapot")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatus.Failed, TerminalStatus(events));

        var terminal = events.Last(e => e.Status == ExecutionStatus.Failed);

        Assert.NotNull(terminal.Error);
        Assert.Contains("418", terminal.Error.Message);
        Assert.DoesNotContain("transitioned to", terminal.Error.Message);
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
