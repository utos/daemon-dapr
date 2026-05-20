using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

[Collection(DaemonTestCollection.Name)]
public class SubWorkflowTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task SubWorkflow_SimpleCall_ExecutesChildActivities()
    {
        // Arrange — Parent calls child workflow, child has one HTTP GET
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/sub-workflow-simple").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("parent-simple", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "child-simple", "child-step")
            .OnSuccess("end")
            .AddDependentWorkflow("child-simple", child => { child.HttpGet("child-step", "/sub-workflow-simple"); })
            .Build("call-child");

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Child's HTTP endpoint was hit
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/sub-workflow-simple"));

        // Assert — Execution completed successfully
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
    }

    [Fact]
    public async Task SubWorkflow_ReturnValue_FlowsToParentOnSuccess()
    {
        // Arrange — Child returns data, parent uses it in next HTTP POST
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/child-data").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"fieldFromChild": "hello"}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/parent-post").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"done": true}"""));

        // Build return struct: return the full output
        var returnStruct = new Struct();
        returnStruct.Fields.Add("fieldFromChild", Value.ForString("{{ output.fieldFromChild }}"));

        // Parent transition input: map child's return value to parent's next activity
        var parentTransitionInput = new Struct();
        parentTransitionInput.Fields.Add("childData", Value.ForString("{{ output.fieldFromChild }}"));

        var request = new TestWorkflowBuilder("parent-return", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "child-return", "fetch-data")
            .OnSuccess("use-result", parentTransitionInput)
            .HttpPost("use-result", "/parent-post", """{"received": "{{ input.childData }}"}""")
            .AddDependentWorkflow("child-return", child =>
            {
                child.HttpGet("fetch-data", "/child-data")
                    .OnReturn(returnStruct);
            })
            .Build("call-child");

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Both endpoints were hit
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/child-data"));

        var postCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/parent-post");
        Assert.NotNull(postCall);
        Assert.Contains("hello", postCall!.RequestMessage.Body);
    }

    [Fact]
    public async Task SubWorkflow_NestedCalls_WorksThreeLevelsDeep()
    {
        // Arrange — A calls B, B calls C (HTTP GET)
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/level-c").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"level": "c"}"""));

        var returnC = new Struct();
        returnC.Fields.Add("level", Value.ForString("{{ output.level }}"));

        var returnB = new Struct();
        returnB.Fields.Add("level", Value.ForString("{{ output.level }}"));

        var request = new TestWorkflowBuilder("workflow-a", fixture.MockBaseUrl)
            .WorkflowCall("call-b", "workflow-b", "call-c")
            .OnSuccess("end")
            .AddDependentWorkflow("workflow-b", b =>
            {
                b.WorkflowCall("call-c", "workflow-c", "do-http")
                    .OnReturn(returnB);
            })
            .AddDependentWorkflow("workflow-c", c =>
            {
                c.HttpGet("do-http", "/level-c")
                    .OnReturn(returnC);
            })
            .Build("call-b");

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — C's HTTP endpoint was hit, execution completed
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/level-c"));

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
    }

    [Fact]
    public async Task SubWorkflow_EndWithoutReturn_ParentSeesNullOutput()
    {
        // Arrange — Child ends without return, parent's next activity runs unconditionally
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/child-no-return").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ignored": true}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/parent-after-child").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("parent-no-return", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "child-no-return", "child-http")
            .OnSuccess("after-child")
            .HttpGet("after-child", "/parent-after-child")
            .AddDependentWorkflow("child-no-return", child =>
            {
                child.HttpGet("child-http", "/child-no-return");
                // No return — child just ends implicitly
            })
            .Build("call-child");

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Both endpoints were hit
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/child-no-return"));
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/parent-after-child"));

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
    }

    [Fact]
    public async Task SubWorkflow_ChildFailure_PropagatesAsParentFailure()
    {
        // Arrange — Child calls an endpoint that returns 500
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/child-500").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithBody("Internal Server Error"));

        var request = new TestWorkflowBuilder("parent-failure", fixture.MockBaseUrl)
            .WorkflowCall("call-child", "child-failure", "fail-http")
            .AddDependentWorkflow("child-failure", child => { child.HttpGet("fail-http", "/child-500"); })
            .Build("call-child");

        // Act
        var response = await fixture.DaemonClient.ScheduleExecutionAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Execution failed (poll for status since projection may lag)
        var deadline = DateTime.UtcNow.AddSeconds(10);
        ExecutionStatus status;
        do
        {
            var execution = await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = response.Id });
            status = execution.Status;
            if (status == ExecutionStatus.Failed) break;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(ExecutionStatus.Failed, status);
    }
}
