using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for workflow transition behavior - verifying conditional transitions work correctly.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class TransitionBehaviorTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task ConditionalTransition_WhenConditionTrue_ShouldTakeTransition()
    {
        // Arrange - Create workflow with conditional transition
        // check-value returns {"count": 15}, condition checks if count > 10
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 15}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/high-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"high\"}"));

        var request = new TestWorkflowBuilder("conditional-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("high-value", when: "output.count > 10")
            .HttpGet("high-value", "/high-value")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Verify both activities were called
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        var highValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/high-value");

        Assert.NotNull(checkValueCall);
        Assert.NotNull(highValueCall);
    }

    [Fact]
    public async Task ConditionalTransition_WhenConditionFalse_ShouldSkipTransition()
    {
        // Arrange - Create workflow with conditional transition that evaluates to false
        // check-value returns {"count": 5}, condition checks if count > 10 (false)
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 5}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/high-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"high\"}"));

        var request = new TestWorkflowBuilder("conditional-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("high-value", when: "output.count > 10")
            .HttpGet("high-value", "/high-value")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Verify only check-value was called, high-value was skipped
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        var highValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/high-value");

        Assert.NotNull(checkValueCall);
        Assert.Null(highValueCall); // Should NOT be called
    }

    [Fact]
    public async Task MultipleConditionalTransitions_ShouldTakeFirstMatch()
    {
        // Arrange - Create workflow with multiple conditional transitions
        // First match wins: count = 15, so first condition (> 10) should match
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 15}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/high-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"high\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/medium-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"medium\"}"));

        var request = new TestWorkflowBuilder("multi-conditional-workflow", fixture.MockBaseUrl,
                "1.0.0")
            .HttpGet("check-value", "/check-value")
            .OnSuccess("high-value", when: "output.count > 10")
            .OnSuccess("medium-value", when: "output.count > 5")
            .HttpGet("high-value", "/high-value")
            .HttpGet("medium-value", "/medium-value")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Verify only first matching transition was taken
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        var highValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/high-value");
        var mediumValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/medium-value");

        Assert.NotNull(checkValueCall);
        Assert.NotNull(highValueCall); // First match - should be called
        Assert.Null(mediumValueCall); // Second condition - should NOT be called
    }

    [Fact]
    public async Task DefaultTransition_WhenNoConditionMatches_ShouldTakeFallback()
    {
        // Arrange - Workflow with conditional + default (no condition) transition
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 3}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/high-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"high\"}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/default-handler").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"result\": \"default\"}"));

        var request = new TestWorkflowBuilder("default-transition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("high-value", when: "output.count > 10")
            .OnSuccess("default-handler")
            .HttpGet("high-value", "/high-value")
            .HttpGet("default-handler", "/default-handler")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Default handler should be called
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        var highValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/high-value");
        var defaultCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/default-handler");

        Assert.NotNull(checkValueCall);
        Assert.Null(highValueCall); // Condition false - should NOT be called
        Assert.NotNull(defaultCall); // Default fallback - should be called
    }

    [Fact]
    public async Task InvalidConditionSyntax_IsRefusedAtLoad()
    {
        // Arrange - a condition that does not parse. Since spec 0.0.15 the shared validator parses
        // every expression against the language's grammar when the bundle is loaded, so this never
        // becomes an execution: it is UTOS-E060 (asserted by code, never by wording) before one exists.
        var request = new TestWorkflowBuilder("invalid-condition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "this is invalid syntax")
            .HttpGet("next-activity", "/next")
            .Build("check-value");

        // Act
        var exception = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => fixture.ScheduleAsync(request));

        // Assert
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-E060", exception.Status.Detail);
        // No execution exists to have called anything; the mock server's log is shared across
        // tests, so "nothing was called" is not something this test can assert on it.
    }

    [Fact]
    public async Task MissingExpectedData_EvaluatesFalse_AndTheRuleIsSkipped()
    {
        // Arrange - Condition references a property that doesn't exist in the output. A missing
        // member is `undefined` (api/docs/template-expressions.md § Scope), so `undefined > 10`
        // is false and the rule is skipped rather than the run failing — the old engine threw.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"active\"}")); // No "count" field!

        var request = new TestWorkflowBuilder("missing-data-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "output.count > 10")
            .HttpGet("next-activity", "/next")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to fail
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - the rule did not match, so the run ended without reaching next-activity
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value"));
        Assert.Null(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/next"));
    }

    [Fact]
    public async Task UndefinedVariable_ShouldFailWorkflow()
    {
        // Arrange - Condition references a name that is not in scope. Unlike a missing member,
        // an unknown root is a ReferenceError (UTOS-E120), which fails the run.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 15}"));

        var request = new TestWorkflowBuilder("undefined-variable-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "undefined_variable > 10")
            .HttpGet("next-activity", "/next")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to fail
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Should fail due to undefined variable
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Verify check-value was called but workflow failed during condition evaluation
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        Assert.NotNull(checkValueCall);
    }

    [Fact]
    public async Task NonBooleanConditionResult_ShouldFailWorkflow()
    {
        // Arrange - Condition that evaluates to a non-boolean value (string) should fail
        // (UTOS-E101). Conditions must evaluate to exactly true or false; JavaScript's truthiness
        // is deliberately not honoured — write `output.status !== ""`.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"active\"}"));

        var request = new TestWorkflowBuilder("non-boolean-condition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "output.status")
            .HttpGet("next-activity", "/next")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to fail
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Should fail because condition doesn't evaluate to boolean
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Verify check-value was called but workflow failed during condition evaluation
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        Assert.NotNull(checkValueCall);
    }

    [Fact]
    public async Task A_bare_return_ends_the_run_with_no_result()
    {
        // `result: {}` — a bare `return` in the source format — ends the path with no value, and
        // the next activity is never reached. Spec 0.0.16: this is what `transition: end` became.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"done\": true}"));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/never-reached").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var request = new TestWorkflowBuilder("bare-return-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnEnd(when: "output.done")
            .OnSuccess("never")
            .HttpGet("never", "/never-reached")
            .Build("check-value");

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
        Assert.Null(execution.Result);
        Assert.NotNull(fixture.WireMockServer.LogEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value"));
        Assert.Null(fixture.WireMockServer.LogEntries.FirstOrDefault(e => e.RequestMessage.Path == "/never-reached"));
    }

    [Fact]
    public async Task An_error_action_fails_the_run_with_what_the_author_wrote()
    {
        // What `transition: error` became, and what it could never do: carry a reason. The code
        // is a literal; the message and details are templates rendered in the rule's scope.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-invalid").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"invalid\": true, \"id\": \"o-7\"}"));

        var details = new Struct();
        details.Fields.Add("id", Value.ForString("{{ output.id }}"));
        details.Fields.Add("status", Value.ForString("{{ response.status }}"));

        var request = new TestWorkflowBuilder("error-action-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-invalid")
            .OnError("INVALID_VALUE", "value {{ output.id }} was invalid", details, when: "output.invalid")
            .Build("check-value");

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // The watch stream's terminal frame carries the authored code and message.
        var terminal = events.Last(e => e.Status == ExecutionStatus.Failed);
        Assert.Equal("INVALID_VALUE", terminal.Error.Code);
        Assert.Equal("value o-7 was invalid", terminal.Error.Message);

        // So does the execution, with the rendered details.
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Failed, execution.Summary.Status);
        Assert.Equal("INVALID_VALUE", execution.Summary.Error.Code);
        Assert.Equal("value o-7 was invalid", execution.Summary.Error.Message);
        Assert.Equal("o-7", execution.Summary.Error.Details.Fields["id"].StringValue);
        Assert.Equal(200, execution.Summary.Error.Details.Fields["status"].NumberValue);
    }
}
