using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Storage;

/// <summary>
/// A blob going where § Moving blobs says a value goes: across a transition in the next activity's
/// <c>input</c>, and out of the run in its result.
/// <para>
/// The failure this exists for was silent. Every hop a value takes in this daemon is JSON — a Dapr
/// activity input, an event, a JSON column — and JSON has no kind for a blob, so one reached the far
/// side as an ordinary map with the handle's fields spread through it. <c>response.body</c> kept
/// working throughout, because a response is a declared field and its blob was typed; the same blob
/// put into a transition's <c>input</c> silently stopped being a blob, and the first sign of it was
/// an expression calling <c>text()</c> on something that had no such method.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class TravellingBlobTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task A_stored_blob_crosses_a_transition_and_is_read_from_the_next_activity()
    {
        const string marker = "-=END=-";
        var body = new string('a', 400 * 1024) + marker;

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/travel-source").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(body));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/travel-sink").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        // The handle is put into the second activity's input by the transition, and read there off
        // `input` rather than off `response` — which is the whole point. The second activity has a
        // response of its own, so nothing here could be reading the first one's by accident.
        var carried = new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } };

        var request = new TestWorkflowBuilder("travelling-blob", fixture.MockBaseUrl)
            .HttpGet("fetch", "/travel-source")
            .OnSuccess("forward", carried)
            .HttpGet("forward", "/travel-sink")
            .OnResult(new Struct
            {
                Fields =
                {
                    ["size"] = Value.ForString("{{ input.photo.size }}"),
                    ["type"] = Value.ForString("{{ input.photo.type }}"),
                    ["tail"] = Value.ForString("{{ (await input.photo.text()).slice(-7) }}"),
                },
            })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        var result = details.Result.Fields;
        Assert.Equal(body.Length, (long)result["size"].NumberValue);
        Assert.Equal("text/plain", result["type"].StringValue);
        Assert.Equal(marker, result["tail"].StringValue);
    }

    [Fact]
    public async Task A_blob_in_a_run_s_result_reaches_the_client_as_a_handle()
    {
        // § Out of a run: the result carries a blob as it is, and a client reads its bytes by
        // calling GetBlob with the id. So the result must arrive as a `blob_value` — not as a map of
        // the handle's fields, which is what a client would have to reconstruct a handle from, and
        // which the spec forbids it from doing.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/result-blob").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "image/png")
                .WithBody(new string('p', 300 * 1024)));

        var request = new TestWorkflowBuilder("result-blob", fixture.MockBaseUrl)
            .HttpGet("fetch", "/result-blob")
            .OnResult(new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        var photo = details.Result.Fields["photo"];

        Assert.Equal(
            Utos.Workflows.V1.WorkflowValue.KindOneofCase.BlobValue, photo.KindCase);
        Assert.NotEmpty(photo.BlobValue.Id);
        Assert.Equal("image/png", photo.BlobValue.MediaType);
        Assert.Equal(300 * 1024, photo.BlobValue.Size);

        // Stored, so the bytes are not in the answer. A client fetches them with GetBlob.
        Assert.True(photo.BlobValue.Data.IsEmpty);
    }

    [Fact]
    public async Task An_inline_blob_keeps_its_bytes_all_the_way_out()
    {
        // The other backing, through the same path. Nothing an author writes distinguishes them; the
        // difference a client sees is that `data` is present and there is no id to fetch.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/small-blob").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/octet-stream")
                .WithBody([0x89, 0x50, 0x4E, 0x47]));

        var request = new TestWorkflowBuilder("inline-result-blob", fixture.MockBaseUrl)
            .HttpGet("fetch", "/small-blob")
            .OnResult(new Struct { Fields = { ["bytes"] = Value.ForString("{{ response.body }}") } })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        var blob = details.Result.Fields["bytes"].BlobValue;

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], blob.Data.ToByteArray());
        Assert.Equal(4, blob.Size);
        Assert.Empty(blob.Id);
    }
}
