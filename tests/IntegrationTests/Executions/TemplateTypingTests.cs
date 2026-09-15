using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for how transform values are typed. A transform value that is a single whole
/// expression keeps its native type, so numbers stay numbers and objects stay objects on the
/// next hop; anything with surrounding text is interpolation and stays a string.
///
/// Before this, every transform value was rendered to a string, so "{{ input.n + 1 }}"
/// concatenated ("1" + 1 = "11") instead of adding — which silently broke counters threaded
/// across a loop's back-edge.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class TemplateTypingTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task WholeExpression_KeepsNumber_SoCountersAccumulateAcrossALoop()
    {
        // Arrange - the idiom that used to produce 0, 01, 011, 0111...
        const string path = "/typing-counter";
        StubJson(path, """{"ok": true}""");

        var increment = new Struct();
        increment.Fields.Add("count", Value.ForString("{{ input.count + 1 }}"));

        var request = new TestWorkflowBuilder("typing-counter-workflow", fixture.MockBaseUrl)
            .HttpGet("poll", path)
            .WithHeader("X-Count", "{{ input.count }}")
            .OnSuccess("poll", increment, when: "input.count < 3")
            .OnSuccess("end")
            .WithInput("count", 0)
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert - arithmetic, not concatenation, and the numeric condition terminates it

        await AssertCompleted(response.Id);
        Assert.Equal(new[] { "0", "1", "2", "3" }, HeadersSent(path, "X-Count"));
    }

    [Fact]
    public async Task InterpolatedExpression_StaysAString()
    {
        // Arrange - surrounding text means the author wants text
        const string path = "/typing-interpolated";
        StubJson(path, """{"ok": true}""");

        var transform = new Struct();
        transform.Fields.Add("label", Value.ForString("user-{{ input.id }}"));

        var request = new TestWorkflowBuilder("typing-interpolated-workflow", fixture.MockBaseUrl)
            .HttpGet("first", path)
            .OnSuccess("second", transform)
            .HttpGet("second", path)
            .WithHeader("X-Label", "{{ input.label }}")
            .WithInput("id", 42)
            .Build("first");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert

        await AssertCompleted(response.Id);
        Assert.Contains("user-42", HeadersSent(path, "X-Label"));
    }

    [Fact]
    public async Task WholeExpression_KeepsNestedObject_SoItsFieldsStayReachable()
    {
        // Arrange - the first activity returns a nested object; the transform forwards it
        // whole, and the next activity reads a field off it. Stringifying would make
        // "{{ input.user.name }}" unreachable.
        const string path = "/typing-object";
        const string next = "/typing-object-next";
        StubJson(path, """{"user": {"name": "ada", "id": 7}}""");
        StubJson(next, """{"ok": true}""");

        var transform = new Struct();
        transform.Fields.Add("user", Value.ForString("{{ output.user }}"));

        var request = new TestWorkflowBuilder("typing-object-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch", path)
            .OnSuccess("use", transform)
            .HttpGet("use", next)
            .WithHeader("X-Name", "{{ input.user.name }}")
            .Build("fetch");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert

        await AssertCompleted(response.Id);
        Assert.Contains("ada", HeadersSent(next, "X-Name"));
    }

    [Fact]
    public async Task RunInput_ReachesATemplateAsAnInteger_NotADouble()
    {
        // The run-input door used to hand the template engine a raw double while the document
        // door narrowed to int. It was masked end to end, because a double whose value is whole
        // renders identically ("0", not "0.0") and the persistence round trip narrowed it back
        // before any template saw it — so no existing test could see the difference.
        //
        // A large id is where it becomes visible: .NET renders a double with the shortest
        // round-trip format, so 10^16 went into the URL as "1E+16" and the daemon called an
        // endpoint that does not exist. The id is 2^53 now — the largest integer an expression
        // accepts (UTOS-E104 beyond it) and, at sixteen digits, still one .NET would write with
        // an exponent; the engine renders whole numbers as digits.
        const string path = "/typing-large-id/9007199254740992";
        StubJson(path, """{"ok": true}""");

        var request = new TestWorkflowBuilder("typing-large-id-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch", "/typing-large-id/{{ input.id }}")
            .WithInput("id", 9_007_199_254_740_992L)
            .Build("fetch");

        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        await AssertCompleted(response.Id);
        Assert.NotEmpty(fixture.WireMockServer.LogEntries
            .Where(e => e.RequestMessage.Path == path));
    }

    [Fact]
    public async Task WholeExpression_KeepsBoolean_SoItDrivesAConditionDirectly()
    {
        // Arrange - a boolean forwarded through a transform must still be a boolean, not
        // the string "true", when the next transition tests it
        const string path = "/typing-bool";
        const string taken = "/typing-bool-taken";
        const string skipped = "/typing-bool-skipped";
        StubJson(path, """{"ready": true}""");
        StubJson(taken, """{"ok": true}""");
        StubJson(skipped, """{"ok": true}""");

        var transform = new Struct();
        transform.Fields.Add("ready", Value.ForString("{{ output.ready }}"));

        var request = new TestWorkflowBuilder("typing-bool-workflow", fixture.MockBaseUrl)
            .HttpGet("check", path)
            .OnSuccess("decide", transform)
            .HttpGet("decide", path)
            .OnSuccess("taken", when: "input.ready")
            .OnSuccess("skipped")
            .HttpGet("taken", taken)
            .HttpGet("skipped", skipped)
            .Build("check");

        // Act
        var response = await fixture.ScheduleAsync(request);
        await WatchToCompletion(response.Id);

        // Assert

        await AssertCompleted(response.Id);
        Assert.Equal(1, CallCount(taken));
        Assert.Equal(0, CallCount(skipped));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private void StubJson(string path, string body) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));

    private int CallCount(string path) =>
        fixture.WireMockServer.LogEntries.Count(e => e.RequestMessage.Path == path);

    private List<string> HeadersSent(string path, string header) =>
        fixture.WireMockServer.LogEntries
            .Where(e => e.RequestMessage.Path == path)
            .SelectMany(e => e.RequestMessage.Headers!
                .Where(h => h.Key == header)
                .Select(h => h.Value.First()))
            .ToList();

    private async Task AssertCompleted(string executionId)
    {
        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = executionId });

        Assert.True(execution.Summary.Status == ExecutionStatus.Completed,
            $"Expected Completed but was {execution.Summary.Status}: {execution.Summary.Error?.Message}");
    }

    private async Task WatchToCompletion(string executionId)
    {
        var call = fixture.ObservabilityClient.WatchExecution(new WatchExecutionRequest
        {
            ExecutionId = executionId
        });

        await call.CollectUntilCompletion(TimeSpan.FromSeconds(60));
    }
}
