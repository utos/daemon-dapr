using System.Text.Json;
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
        var response = await fixture.ScheduleAsync(request);

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
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
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
                    .OnResult(returnStruct);
            })
            .Build("call-child");

        // Act
        var response = await fixture.ScheduleAsync(request);

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
                    .OnResult(returnB);
            })
            .AddDependentWorkflow("workflow-c", c =>
            {
                c.HttpGet("do-http", "/level-c")
                    .OnResult(returnC);
            })
            .Build("call-b");

        // Act
        var response = await fixture.ScheduleAsync(request);

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
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
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
        var response = await fixture.ScheduleAsync(request);

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
        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
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
        var response = await fixture.ScheduleAsync(request);

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
            status = execution.Summary.Status;
            if (status == ExecutionStatus.Failed) break;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(ExecutionStatus.Failed, status);
    }

    [Fact]
    public async Task DetachedSubWorkflow_ParentDoesNotWait_ChildRunsAsOwnExecution()
    {
        // Arrange — The child waits, then hits an endpoint. Because the parent starts it
        // detached, the parent should finish long before the child's delayed work runs.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/detached-child").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"done": true}"""));

        // The parent posts the detached handle here so the test can read the child's id.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/parent-after").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));

        // The detached activity's output is a handle { execution_id }, not the child's result.
        var handleToInput = new Struct();
        handleToInput.Fields.Add("childId", Value.ForString("{{ output.execution_id }}"));

        var request = new TestWorkflowBuilder("parent-detached", fixture.MockBaseUrl)
            .WorkflowCall("start-child", "child-detached", "wait", detached: true)
            .OnSuccess("after", handleToInput)
            .HttpPost("after", "/parent-after", """{"childId": "{{ input.childId }}"}""")
            .OnSuccess("end")
            .AddDependentWorkflow("child-detached", child =>
            {
                child.Timer("wait", TimeSpan.FromSeconds(6)).OnSuccess("hit");
                child.HttpGet("hit", "/detached-child");
            })
            .Build("start-child");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // The parent's own stream closes when the parent finishes — the detached child logs
        // to its own execution id, so it does not keep the parent's stream open.
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert — Parent completed
        var parent = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });
        Assert.Equal(ExecutionStatus.Completed, parent.Summary.Status);

        // Assert — Fire-and-forget: the child's delayed work has NOT run yet at parent completion.
        // If the parent had awaited the child, the 6s timer would have gated its completion.
        Assert.DoesNotContain(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/detached-child");

        // Assert — The activity output handle carried the child's execution id.
        var postCall = fixture.WireMockServer.LogEntries
            .FirstOrDefault(e => e.RequestMessage.Path == "/parent-after");
        Assert.NotNull(postCall);
        var childId = JsonDocument.Parse(postCall!.RequestMessage.Body!)
            .RootElement.GetProperty("childId").GetString();
        Assert.False(string.IsNullOrEmpty(childId));
        Assert.True(Guid.TryParse(childId, out _));
        Assert.NotEqual(response.Id, childId);

        // Assert — The detached child is its own top-level execution and appears in ListExecutions.
        await WaitUntil(async () =>
        {
            var list = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
            return list.Executions.Any(e => e.Id == childId);
        }, TimeSpan.FromSeconds(15));

        var listed = await fixture.DaemonClient.ListExecutionsAsync(new ListExecutionsRequest());
        Assert.Contains(listed.Executions, e => e.Id == childId);
        Assert.Contains(listed.Executions, e => e.Id == response.Id);

        // Assert — The child completes independently, on its own, and ran its own activity.
        ExecutionStatus childStatus = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            var child = await fixture.DaemonClient.GetExecutionAsync(
                new GetExecutionRequest { ExecutionId = childId });
            childStatus = child.Summary.Status;
            if (childStatus is ExecutionStatus.Completed or ExecutionStatus.Failed) break;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(ExecutionStatus.Completed, childStatus);
        Assert.Contains(fixture.WireMockServer.LogEntries,
            e => e.RequestMessage.Path == "/detached-child");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }

        throw new TimeoutException($"Condition not met within {timeout}");
    }
}
