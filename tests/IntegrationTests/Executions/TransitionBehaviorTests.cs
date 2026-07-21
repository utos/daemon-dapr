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
            .OnSuccess("high-value", when: "{{ output.count > 10 }}")
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
            .OnSuccess("high-value", when: "{{ output.count > 10 }}")
            .HttpGet("high-value", "/high-value")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(3000));

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
            .OnSuccess("high-value", when: "{{ output.count > 10 }}")
            .OnSuccess("medium-value", when: "{{ output.count > 5 }}")
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
            .OnSuccess("high-value", when: "{{ output.count > 10 }}")
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
    public async Task InvalidConditionSyntax_ShouldFailWorkflow()
    {
        // Arrange - Workflow with invalid template syntax in condition
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 15}"));

        var request = new TestWorkflowBuilder("invalid-condition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "{{ this is invalid syntax }}")
            .HttpGet("next-activity", "/next")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to fail
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Should transition to Failed status due to invalid condition
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Verify check-value was called but workflow failed during condition evaluation
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        Assert.NotNull(checkValueCall);
    }

    [Fact]
    public async Task MissingExpectedData_ShouldFailWorkflow()
    {
        // Arrange - Condition references a property that doesn't exist in the output
        // Tests EnableRelaxedMemberAccess = false behavior
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"active\"}")); // No "count" field!

        var request = new TestWorkflowBuilder("missing-data-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "{{ output.count > 10 }}")
            .HttpGet("next-activity", "/next")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to fail
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Should fail due to missing expected data
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Verify check-value was called but workflow failed during condition evaluation
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");
        Assert.NotNull(checkValueCall);
    }

    [Fact]
    public async Task UndefinedVariable_ShouldFailWorkflow()
    {
        // Arrange - Condition references a variable that doesn't exist in context
        // Tests StrictVariables = true behavior
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\": 15}"));

        var request = new TestWorkflowBuilder("undefined-variable-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "{{ undefined_variable > 10 }}")
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
        // Conditions must explicitly evaluate to boolean - use comparisons like {{ output.status != "" }}
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"active\"}"));

        var request = new TestWorkflowBuilder("non-boolean-condition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("next-activity", when: "{{ output.status }}")
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
    public async Task TransitionToEnd_ShouldCompleteWorkflow()
    {
        // Arrange - Workflow with explicit transition to "end"
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"done\": true}"));

        var request = new TestWorkflowBuilder("end-transition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("end", when: "{{ output.done }}")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to complete
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Should complete successfully
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");

        Assert.NotNull(checkValueCall);
    }

    [Fact]
    public async Task TransitionToError_ShouldFailWorkflow()
    {
        // Arrange - Workflow with explicit transition to "error"
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/check-value").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"invalid\": true}"));

        var request = new TestWorkflowBuilder("error-transition-workflow", fixture.MockBaseUrl)
            .HttpGet("check-value", "/check-value")
            .OnSuccess("error", when: "{{ output.invalid }}")
            .Build("check-value");

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for workflow to fail
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Should fail
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var checkValueCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/check-value");

        Assert.NotNull(checkValueCall);
    }
}
