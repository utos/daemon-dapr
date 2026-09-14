using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using Google.Protobuf.WellKnownTypes;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for data flow through workflow execution - verifying input/output serialization
/// and template engine access to nested values.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class DataFlowTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task InputData_ShouldBeAccessibleInTemplates()
    {
        // Arrange - Workflow that uses input data in URL template
        fixture.WireMockServer
            .Given(Request.Create()
                .WithPath("/users/42")
                .WithHeader("X-API-Key", "secret-key-123")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\": 42, \"name\": \"Test User\"}"));

        var request = new TestWorkflowBuilder("input-template-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch-user", "/users/{{ input.user_id }}")
            .WithHeader("X-API-Key", "{{ input.api_key }}")
            .WithInput("user_id", 42)
            .WithInput("api_key", "secret-key-123")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Wait for completion
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Verify templated URL and header were used correctly
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var userCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/users/42");

        Assert.NotNull(userCall);
        Assert.Contains(userCall!.RequestMessage!.Headers!, h =>
            h.Key == "X-API-Key" && h.Value.Contains("secret-key-123"));
    }

    [Fact]
    public async Task TransitionInputTransform_ShouldMapOutputToNextActivityInput()
    {
        // Arrange - Multi-activity workflow that chains data via transition input transforms
        // Activity 1: /me returns nested user data
        // Transition transforms output.user.id and forwards input.bearer_token
        // Activity 2: Uses transformed input to fetch /users/789/profile

        fixture.WireMockServer
            .Given(Request.Create()
                .WithPath("/me")
                .WithHeader("Authorization", "Bearer auth-token-abc-123")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                          {
                              "user": {
                                  "id": 789,
                                  "username": "john.doe",
                                  "role": "admin"
                              },
                              "session": {
                                  "token": "session-xyz",
                                  "expires_at": "2025-12-31T23:59:59Z"
                              }
                          }
                          """));

        fixture.WireMockServer
            .Given(Request.Create()
                .WithPath("/users/789/profile")
                .WithHeader("Authorization", "Bearer auth-token-abc-123")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                          {
                              "profile": {
                                  "bio": "Software Engineer",
                                  "location": "San Francisco"
                              }
                          }
                          """));

        // Build the transition input transform: map output fields + forward input fields
        var transitionInput = new Struct();
        transitionInput.Fields.Add("user_id", Value.ForString("{{ output.user.id }}"));
        transitionInput.Fields.Add("bearer_token", Value.ForString("{{ input.bearer_token }}"));

        var request = new TestWorkflowBuilder("transition-transform-workflow", fixture.MockBaseUrl)
            .HttpGet("get-current-user", "/me")
            .WithHeader("Authorization", "Bearer {{ input.bearer_token }}")
            .OnSuccess("get-user-profile", transitionInput)
            .HttpGet("get-user-profile", "/users/{{ input.user_id }}/profile")
            .WithHeader("Authorization", "Bearer {{ input.bearer_token }}")
            .WithInput("bearer_token", "auth-token-abc-123")
            .Build("get-current-user");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Both activities were called with correct parameters
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var meCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/me");
        Assert.NotNull(meCall);

        var profileCall = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/users/789/profile");
        Assert.NotNull(profileCall);
        Assert.Contains(profileCall!.RequestMessage!.Headers!, h =>
            h.Key == "Authorization" && h.Value.Contains("Bearer auth-token-abc-123"));
    }

    [Fact]
    public async Task TransitionWithoutInput_ShouldPassThroughRawOutput()
    {
        // Arrange - Two activities, transition has no input transform
        // The second activity should receive the first's raw output as its input

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/get-config").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"endpoint": "final", "value": "hello"}"""));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/final").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"status": "ok"}"""));

        var request = new TestWorkflowBuilder("passthrough-workflow", fixture.MockBaseUrl)
            .HttpGet("get-config", "/get-config")
            .OnSuccess("use-config")
            .HttpGet("use-config", "/{{ input.endpoint }}")
            .Build("get-config");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Second activity used first activity's output as input
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/get-config"));
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/final"));
    }

    [Fact]
    public async Task TransitionInputTransform_ShouldForwardInputAcrossMultipleActivities()
    {
        // Arrange - Three activities: A → B → C
        // A receives workflow input (api_key), transition A→B maps output + forwards api_key
        // B receives mapped input, transition B→C forwards B's input.api_key (not workflow input)

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/step-a").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"user_id": 55}"""));

        fixture.WireMockServer
            .Given(Request.Create()
                .WithPath("/step-b/55")
                .WithHeader("X-Key", "my-secret")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"result": "data-from-b"}"""));

        fixture.WireMockServer
            .Given(Request.Create()
                .WithPath("/step-c")
                .WithHeader("X-Key", "my-secret")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"done": true}"""));

        // A→B: forward output.user_id and input.api_key
        var abTransform = new Struct();
        abTransform.Fields.Add("user_id", Value.ForString("{{ output.user_id }}"));
        abTransform.Fields.Add("api_key", Value.ForString("{{ input.api_key }}"));

        // B→C: forward input.api_key (from B's input, not workflow input)
        var bcTransform = new Struct();
        bcTransform.Fields.Add("api_key", Value.ForString("{{ input.api_key }}"));

        var request = new TestWorkflowBuilder("multi-hop-forward-workflow", fixture.MockBaseUrl)
            .HttpGet("step-a", "/step-a")
            .OnSuccess("step-b", abTransform)
            .HttpGet("step-b", "/step-b/{{ input.user_id }}")
            .WithHeader("X-Key", "{{ input.api_key }}")
            .OnSuccess("step-c", bcTransform)
            .HttpGet("step-c", "/step-c")
            .WithHeader("X-Key", "{{ input.api_key }}")
            .WithInput("api_key", "my-secret")
            .Build("step-a");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - All three activities called with correct data
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/step-a"));
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/step-b/55"));

        var stepC = logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/step-c");
        Assert.NotNull(stepC);
        Assert.Contains(stepC!.RequestMessage!.Headers!, h =>
            h.Key == "X-Key" && h.Value.Contains("my-secret"));
    }

    [Fact]
    public async Task ComplexNestedInput_ShouldBeAccessibleInTemplates()
    {
        // Arrange - Test deeply nested input structure
        var addressStruct = new Struct();
        addressStruct.Fields.Add("street", Value.ForString("123 Main St"));
        addressStruct.Fields.Add("city", Value.ForString("San Francisco"));
        addressStruct.Fields.Add("zip", Value.ForString("94102"));

        var userStruct = new Struct();
        userStruct.Fields.Add("name", Value.ForString("Jane Smith"));
        userStruct.Fields.Add("age", Value.ForNumber(30));
        userStruct.Fields.Add("address", Value.ForStruct(addressStruct));

        var input = new ExecutionPayload();
        input.Data.Add("user", Value.ForStruct(userStruct));
        input.Data.Add("timestamp", Value.ForString("2025-01-01T00:00:00Z"));

        fixture.WireMockServer
            .Given(Request.Create()
                .WithPath("/search")
                .WithParam("city", new ExactMatcher("San Francisco"))
                .WithParam("zip", new ExactMatcher("94102"))
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"results\": []}"));

        var request = new TestWorkflowBuilder("complex-nested-input-workflow", fixture.MockBaseUrl)
            .HttpGet("search-location",
                "/search?city={{ input.user.address.city }}&zip={{ input.user.address.zip }}")
            .WithInput(input)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Verify nested properties were accessed correctly
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        var searchCall = logEntries.FirstOrDefault(e =>
            e.RequestMessage is { Path: "/search", Query: not null } &&
            e.RequestMessage.Query.ContainsKey("city") &&
            e.RequestMessage.Query["city"].Contains("San Francisco") &&
            e.RequestMessage.Query.ContainsKey("zip") &&
            e.RequestMessage.Query["zip"].Contains("94102"));

        Assert.NotNull(searchCall);
    }

    [Fact]
    public async Task OutputWithArrays_ShouldBeAccessibleInConditions()
    {
        // Arrange - Activity returns array, condition checks array properties
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/items").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                          {
                              "items": [
                                  { "id": 1, "name": "Item 1" },
                                  { "id": 2, "name": "Item 2" },
                                  { "id": 3, "name": "Item 3" }
                              ],
                              "total": 3
                          }
                          """));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/process-many").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"processed\"}"));

        var request = new TestWorkflowBuilder("array-output-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch-items", "/items")
            .OnSuccess("process-many", when: "output.total > 2")
            .HttpGet("process-many", "/process-many")
            .Build("fetch-items");

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        // Assert - Both activities should be called
        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/items"));
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/process-many"));
    }

    [Fact]
    public async Task NullInputValues_ShouldBeHandledGracefully()
    {
        // Arrange - Input with null values
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/users/123").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\": 123}"));

        var request = new TestWorkflowBuilder("null-input-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch-user", "/users/{{ input.user_id }}")
            .WithInput("user_id", 123)
            .WithInput("optional_field", null)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = response.Id
        });

        // Assert - Should complete successfully despite null value
        await call.CollectUntilCompletion(TimeSpan.FromSeconds(30));

        var logEntries = fixture.WireMockServer.LogEntries.ToList();
        Assert.NotNull(logEntries.FirstOrDefault(e => e.RequestMessage.Path == "/users/123"));
    }
}
