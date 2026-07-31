using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for the ambient environment introduced in Utos v0.0.10: config supplied at
/// schedule time that every activity can reach via {{ env.x }}, in contrast to `input`,
/// which reaches only the start activity.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class AmbientEnvTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task Env_ShouldResolveInHttpUrl()
    {
        // Arrange - the base URL comes from env rather than being baked into the bundle
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-url").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("env-url-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch", "/ignored", customUrl: "{{ env.baseUrl }}/env-url")
            .WithEnv("baseUrl", fixture.MockBaseUrl)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert
        Assert.Contains(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/env-url");

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }

    [Fact]
    public async Task Env_ShouldResolveInHeaderAndBody()
    {
        // Arrange
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-header-body").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("env-header-body-workflow", fixture.MockBaseUrl)
            .HttpPost("send", "/env-header-body", body: """{"region": "{{ env.region }}"}""")
            .WithHeader("X-Region", "{{ env.region }}")
            .WithEnv("region", "eu-west-1")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert
        var call = fixture.WireMockServer.LogEntries
            .FirstOrDefault(e => e.RequestMessage.Path == "/env-header-body");

        Assert.NotNull(call);
        Assert.Contains(call!.RequestMessage!.Headers!,
            h => h.Key == "X-Region" && h.Value.Contains("eu-west-1"));
        Assert.Contains("eu-west-1", call.RequestMessage.Body);
    }

    [Fact]
    public async Task Env_ShouldReachNonStartActivity()
    {
        // Arrange - this is the property that distinguishes env from input: the second
        // activity receives no input from the transition, yet still resolves {{ env.x }}
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-first").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"step": 1}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-second/tenant-9").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"step": 2}"""));

        var request = new TestWorkflowBuilder("env-non-start-workflow", fixture.MockBaseUrl)
            .HttpGet("first", "/env-first")
            .OnSuccess("second")
            .HttpGet("second", "/env-second/{{ env.tenant }}")
            .WithEnv("tenant", "tenant-9")
            .Build("first");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert
        Assert.Contains(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/env-second/tenant-9");
    }

    [Fact]
    public async Task Env_ShouldBeAvailableInTransitionCondition()
    {
        // Arrange - env decides which branch the transition takes
        foreach (var path in new[] { "/env-cond-start", "/env-cond-enabled", "/env-cond-disabled" })
            fixture.WireMockServer
                .Given(Request.Create().WithPath(path).UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"ok": true}"""));

        var request = new TestWorkflowBuilder("env-condition-workflow", fixture.MockBaseUrl)
            .HttpGet("start", "/env-cond-start")
            .OnSuccess("enabled-path", when: "{{ env.featureFlag == \"on\" }}")
            .OnSuccess("disabled-path")
            .HttpGet("enabled-path", "/env-cond-enabled")
            .HttpGet("disabled-path", "/env-cond-disabled")
            .WithEnv("featureFlag", "on")
            .Build("start");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert - the env-gated branch ran and the fallback did not
        var paths = fixture.WireMockServer.LogEntries
            .Select(e => e.RequestMessage.Path).ToList();

        Assert.Contains("/env-cond-enabled", paths);
        Assert.DoesNotContain("/env-cond-disabled", paths);
    }

    [Fact]
    public async Task Env_ShouldBeAvailableInPromiseForEachCollection()
    {
        // Arrange - a promise branch conditioned on env, fanning out over input items
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-foreach-item").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"processed": true}"""));

        var branchInput = new Struct();
        branchInput.Fields.Add("itemValue", Value.ForString("{{ item }}"));
        branchInput.Fields.Add("stage", Value.ForString("{{ env.stage }}"));

        var request = new TestWorkflowBuilder("env-foreach-parent", fixture.MockBaseUrl)
            .WorkflowCall("call-item-wf", "env-child-foreach", "http-step")
            .Promise("fan-out", "all")
            .ForEachBranch("item", "call-item-wf", "{{ input.items }}", "item", branchInput)
            .OnSuccess("end")
            .AddDependentWorkflow("env-child-foreach",
                child => { child.HttpGet("http-step", "/env-foreach-item"); })
            .WithInput("items", Value.ForList(Value.ForString("a"), Value.ForString("b")))
            .WithEnv("stage", "staging")
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert - both branches expanded and ran
        Assert.Equal(2, fixture.WireMockServer.LogEntries
            .Count(e => e.RequestMessage.Path == "/env-foreach-item"));
    }

    [Fact]
    public async Task Env_ShouldBeInheritedBySubWorkflow()
    {
        // Arrange - only the parent is scheduled with env; the child references it
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-child/inherited-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("env-parent-workflow", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "env-child-workflow", "child-step")
            .OnSuccess("end")
            .AddDependentWorkflow("env-child-workflow",
                child => { child.HttpGet("child-step", "/env-child/{{ env.sharedKey }}"); })
            .WithEnv("sharedKey", "inherited-value")
            .Build("call-child");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert - the child resolved the parent's env
        Assert.Contains(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/env-child/inherited-value");
    }

    [Fact]
    public async Task Env_ShouldBeInheritedByPromiseBranch()
    {
        // Arrange - a promise branch is a child execution like any other, so the workflow it
        // runs must see the parent's env. Note this is env used *inside* the branch, not in
        // the branch's input mapping, which the parent renders before the branch starts.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/env-branch/branch-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("env-promise-parent", fixture.MockBaseUrl)
            .WorkflowCall("call-branch-wf", "env-branch-child", "branch-step")
            .Promise("fan-out", "all")
            .Branch("only", "call-branch-wf")
            .OnSuccess("end")
            .AddDependentWorkflow("env-branch-child",
                child => { child.HttpGet("branch-step", "/env-branch/{{ env.sharedKey }}"); })
            .WithEnv("sharedKey", "branch-value")
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert - the branch resolved the parent's env
        Assert.Contains(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/env-branch/branch-value");

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }

    [Fact]
    public async Task GetExecution_ShouldEchoBackEnv()
    {
        // Arrange
        var request = new TestWorkflowBuilder("env-echo-workflow", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .WithEnv("region", "eu-west-1")
            .WithEnv("baseUrl", "https://api.example.test")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        // Assert - echoed back after the run finished, so it survives the execution
        // context row being deleted at completion
        Assert.Equal(2, execution.Env.Count);
        Assert.Equal("eu-west-1", execution.Env["region"]);
        Assert.Equal("https://api.example.test", execution.Env["baseUrl"]);
    }

    [Fact]
    public async Task WithoutEnv_ShouldStillExecute()
    {
        // Arrange - regression guard for always setting an empty env rather than leaving
        // the template root undefined
        var request = new TestWorkflowBuilder("env-absent-workflow", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
        Assert.Empty(execution.Env);
    }

    [Theory]
    [InlineData("not-a-url", "invalid url format")]
    [InlineData("ftp://wrong-scheme.test", "unsupported url scheme")]
    public async Task Env_TemplatedUrlRenderingToGarbage_ShouldFailWithTheSameDiagnostic(
        string envBaseUrl, string expectedReason)
    {
        // Arrange - a templated URL skips the load-time absolute-URL check (its host is
        // not known until rendered), so the same rules are re-applied after rendering.
        // A bad value must fail with that diagnostic, not a raw HTTP client error.
        var request = new TestWorkflowBuilder("env-bad-url-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch", "/ignored", customUrl: "{{ env.baseUrl }}/thing")
            .WithEnv("baseUrl", envBaseUrl)
            .Build();

        // Act - schedule succeeds; the failure surfaces during execution
        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        // Assert
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Failed, execution.Summary.Status);
        Assert.NotNull(execution.Summary.Error);

        var message = execution.Summary.Error.Message.ToLowerInvariant();
        Assert.Contains(expectedReason, message);
        Assert.Contains("fetch", message); // names the offending activity
    }

}
