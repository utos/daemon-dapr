using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// The Node globals of spec 0.0.16 end to end: a binary response body reaches an expression as a
/// <c>Buffer</c>, and the captured instant makes every expression of one decision agree about
/// the time. The values themselves are the evaluation corpus's; what these prove is the plumbing
/// from an HTTP response and a Dapr activity to the engine.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class NodeGlobalsTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task A_binary_response_body_is_a_buffer_in_scope()
    {
        // A PNG signature with a content type nothing parses. `output` is null for it; the bytes
        // are on `response.body`, and leave the expression as the text the author chose.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/binary-body").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithBody(png));

        var result = new Struct();
        result.Fields.Add("hex", Value.ForString("{{ response.body.toString('hex') }}"));
        result.Fields.Add("length", Value.ForString("{{ response.body.length }}"));
        result.Fields.Add("isPng", Value.ForString("{{ response.body.readUInt8(0) === 0x89 && response.body.slice(1, 4).toString('latin1') === 'PNG' }}"));
        result.Fields.Add("digest", Value.ForString("{{ crypto.createHash('sha256').update(response.body).digest('base64url') }}"));

        var request = new TestWorkflowBuilder("binary-body-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch", "/binary-body")
            .OnResult(result)
            .Build("fetch");

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
        Assert.Equal("89504e470d0a1a0a", execution.Result.Fields["hex"].StringValue);
        Assert.Equal(8, execution.Result.Fields["length"].NumberValue);
        Assert.True(execution.Result.Fields["isPng"].BoolValue);
        Assert.Equal(
            Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(png)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            execution.Result.Fields["digest"].StringValue);
    }

    [Fact]
    public async Task Every_expression_of_one_decision_sees_the_same_instant()
    {
        // Two fields, two ids, one activity evaluation: the instant is captured once, so the
        // fields agree to the millisecond; the ids come from one seed-derived sequence, so they
        // differ from each other. And the instant is real — the activity ran now, not at the
        // epoch a scope with no moment evaluates at.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/now").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        var result = new Struct();
        result.Fields.Add("a", Value.ForString("{{ new Date().toISOString() }}"));
        result.Fields.Add("b", Value.ForString("{{ new Date(Date.now()).toISOString() }}"));
        result.Fields.Add("id1", Value.ForString("{{ crypto.randomUUID() }}"));
        result.Fields.Add("id2", Value.ForString("{{ crypto.randomUUID() }}"));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var request = new TestWorkflowBuilder("instant-workflow", fixture.MockBaseUrl)
            .HttpGet("fetch", "/now")
            .OnResult(result)
            .Build("fetch");

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);

        var a = execution.Result.Fields["a"].StringValue;
        Assert.Equal(a, execution.Result.Fields["b"].StringValue);
        Assert.InRange(DateTimeOffset.Parse(a), before, DateTimeOffset.UtcNow.AddSeconds(1));

        var id1 = execution.Result.Fields["id1"].StringValue;
        var id2 = execution.Result.Fields["id2"].StringValue;
        Assert.NotEqual(id1, id2);
        Assert.True(Guid.TryParse(id1, out _));
    }
}
