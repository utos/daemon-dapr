using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

[Collection(DaemonTestCollection.Name)]
public class PromiseTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task Promise_AllMode_AllBranchesSucceed()
    {
        // Arrange — 2 branches, each calling a dependent workflow with an HTTP GET
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-all-branch1").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "from-branch1"}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-all-branch2").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "from-branch2"}"""));

        var returnStruct = new Struct();
        returnStruct.Fields.Add("result", Value.ForString("{{ output.result }}"));

        var promiseReturnStruct = new Struct();
        promiseReturnStruct.Fields.Add("branch1", Value.ForString("{{ output.branch1.result }}"));
        promiseReturnStruct.Fields.Add("branch2", Value.ForString("{{ output.branch2.result }}"));

        var request = new TestWorkflowBuilder("promise-all-parent", fixture.MockBaseUrl)
            // Branch target activities (workflow-type) in the parent workflow
            // Promise activity references those workflow-call activities
            .Promise("fan-out", "all")
            .Branch("branch1", "child-wf1", "http-step")
            .Branch("branch2", "child-wf2", "http-step")
            .OnResult(promiseReturnStruct)
            // Dependent workflows
            .AddDependentWorkflow("child-wf1", child =>
            {
                child.HttpGet("http-step", "/promise-all-branch1")
                    .OnResult(returnStruct);
            })
            .AddDependentWorkflow("child-wf2", child =>
            {
                child.HttpGet("http-step", "/promise-all-branch2")
                    .OnResult(returnStruct);
            })
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Both endpoints were hit
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/promise-all-branch1"));
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/promise-all-branch2"));

        // Assert — Execution completed successfully
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }

    [Fact]
    public async Task Promise_AllMode_BranchFailure_Propagates()
    {
        // Arrange — 2 branches, one succeeds, one returns 500
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-allfail-ok").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "ok"}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-allfail-500").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithBody("Internal Server Error"));

        var request = new TestWorkflowBuilder("promise-allfail-parent", fixture.MockBaseUrl)
            .Promise("fan-out", "all")
            .Branch("ok-branch", "child-ok", "http-step")
            .Branch("fail-branch", "child-fail", "http-step")
            .OnEnd()
            .AddDependentWorkflow("child-ok", child => { child.HttpGet("http-step", "/promise-allfail-ok"); })
            .AddDependentWorkflow("child-fail", child => { child.HttpGet("http-step", "/promise-allfail-500"); })
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Execution failed
        var deadline = DateTime.UtcNow.AddSeconds(10);
        ExecutionStatus status;
        do
        {
            var execution = await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = response.Id });
            status = execution.Summary.Status;
            if (status == ExecutionStatus.Failed) break;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(ExecutionStatus.Failed, status);
    }

    [Fact]
    public async Task Promise_AnyMode_FirstSuccessWins()
    {
        // Arrange — 2 branches, first fails (500), second succeeds
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-any-fail").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithBody("Internal Server Error"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-any-ok").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "any-winner"}"""));

        var returnStruct = new Struct();
        returnStruct.Fields.Add("result", Value.ForString("{{ output.result }}"));

        var request = new TestWorkflowBuilder("promise-any-parent", fixture.MockBaseUrl)
            .Promise("fan-out", "any")
            .Branch("fail-branch", "child-any-fail", "http-step")
            .Branch("ok-branch", "child-any-ok", "http-step")
            .OnEnd()
            .AddDependentWorkflow("child-any-fail", child => { child.HttpGet("http-step", "/promise-any-fail"); })
            .AddDependentWorkflow("child-any-ok", child =>
            {
                child.HttpGet("http-step", "/promise-any-ok")
                    .OnResult(returnStruct);
            })
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Execution completed (any mode: one success is enough)
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }

    [Fact]
    public async Task Promise_RaceMode_FirstSettlementWins()
    {
        // Arrange — 2 branches, both succeed (race picks whichever settles first)
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-race-a").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "racer-a"}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-race-b").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "racer-b"}"""));

        var returnStruct = new Struct();
        returnStruct.Fields.Add("result", Value.ForString("{{ output.result }}"));

        var request = new TestWorkflowBuilder("promise-race-parent", fixture.MockBaseUrl)
            .Promise("fan-out", "race")
            .Branch("racer-a", "child-race-a", "http-step")
            .Branch("racer-b", "child-race-b", "http-step")
            .OnEnd()
            .AddDependentWorkflow("child-race-a", child =>
            {
                child.HttpGet("http-step", "/promise-race-a")
                    .OnResult(returnStruct);
            })
            .AddDependentWorkflow("child-race-b", child =>
            {
                child.HttpGet("http-step", "/promise-race-b")
                    .OnResult(returnStruct);
            })
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Execution completed, one of the racers won
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }

    [Fact]
    public async Task Promise_ForEach_ExpandsBranches()
    {
        // Arrange — 1 branch with for_each over a 3-item collection from input
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promise-foreach-item").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"processed": true}"""));

        var returnStruct = new Struct();
        returnStruct.Fields.Add("processed", Value.ForString("{{ output.processed }}"));

        var branchInput = new Struct();
        branchInput.Fields.Add("itemValue", Value.ForString("{{ item }}"));

        var request = new TestWorkflowBuilder("promise-foreach-parent", fixture.MockBaseUrl)
            .Promise("fan-out", "all")
            .ForEachBranch("item", "child-foreach", "http-step",
                "{{ input.items }}", "item", branchInput)
            .OnEnd()
            .AddDependentWorkflow("child-foreach", child =>
            {
                child.HttpGet("http-step", "/promise-foreach-item")
                    .OnResult(returnStruct);
            })
            .WithInput("items", Value.ForList(
                Value.ForString("alpha"),
                Value.ForString("beta"),
                Value.ForString("gamma")))
            .Build("fan-out");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — The endpoint was hit 3 times (once per item)
        var logEntries = fixture.WireMockServer.LogEntries
            .Where(e => e.RequestMessage.Path == "/promise-foreach-item")
            .ToList();
        Assert.Equal(3, logEntries.Count);

        // Assert — Execution completed
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
    }
}
