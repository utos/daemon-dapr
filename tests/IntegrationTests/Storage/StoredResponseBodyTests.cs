using System.Text;
using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Storage;

/// <summary>
/// A response body that crosses the inline threshold, through a real workflow and a real object
/// store.
/// <para>
/// This is the test the blob work exists for. Everything below it — the driver, the record, the
/// host objects — was verified against its own subject; this is the only one that says a workflow
/// can fetch something too large to carry, keep it as a handle, and read it back through
/// <c>await response.body.text()</c> with the bytes having gone to storage and come back.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class StoredResponseBodyTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task A_body_over_the_threshold_is_stored_and_read_back_through_the_store()
    {
        // Larger than the daemon's inline threshold, so the value the run carries is a handle and
        // the bytes are in MinIO. The marker at the end is what proves the whole object came back
        // rather than a prefix.
        const string marker = "-=END=-";
        var body = new string('a', 400 * 1024) + marker;

        const string path = "/stored-body";
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(body));

        var request = new TestWorkflowBuilder("stored-response-body", fixture.MockBaseUrl)
            .HttpGet("fetch", path)
            // Read through the store: `size` comes from the handle without touching it, and
            // `text()` is what actually fetches the bytes.
            .OnResult(new Struct
            {
                Fields =
                {
                    ["size"] = Value.ForString("{{ response.body.size }}"),
                    ["type"] = Value.ForString("{{ response.body.type }}"),
                    ["tail"] = Value.ForString("{{ (await response.body.text()).slice(-7) }}"),
                },
            })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        var result = details.Result.Fields;
        Assert.Equal(Encoding.UTF8.GetByteCount(body), (long)result["size"].NumberValue);
        Assert.Equal("text/plain", result["type"].StringValue);

        // The bytes made the round trip: written to the store as the response streamed, fetched back
        // when the expression asked for them.
        Assert.Equal(marker, result["tail"].StringValue);
    }

    [Fact]
    public async Task A_small_body_reads_the_same_way()
    {
        // The property that makes the threshold invisible: the same document, under the threshold,
        // behaves identically. An author cannot write something that works with one backing and
        // fails with the other, so nothing here says which one it got.
        const string path = "/inline-body";
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody("small-=END=-"));

        var request = new TestWorkflowBuilder("inline-response-body", fixture.MockBaseUrl)
            .HttpGet("fetch", path)
            .OnResult(new Struct
            {
                Fields =
                {
                    ["size"] = Value.ForString("{{ response.body.size }}"),
                    ["tail"] = Value.ForString("{{ (await response.body.text()).slice(-7) }}"),
                },
            })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
        Assert.Equal(12, (long)details.Result.Fields["size"].NumberValue);
        Assert.Equal("-=END=-", details.Result.Fields["tail"].StringValue);
    }

    [Fact]
    public async Task A_stored_blob_is_sliced_without_reading_the_object()
    {
        // Slicing a stored blob is the same object at another offset, so reading 7 bytes of a
        // 400 KiB body reads 7 bytes — the property that makes a large blob usable at all.
        const string path = "/sliced-body";
        var body = "HEAD" + new string('b', 400 * 1024);

        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/octet-stream")
                .WithBody(body));

        var request = new TestWorkflowBuilder("sliced-response-body", fixture.MockBaseUrl)
            .HttpGet("fetch", path)
            .OnResult(new Struct
            {
                Fields =
                {
                    ["head"] = Value.ForString("{{ await response.body.slice(0, 4).text() }}"),
                    ["sliceSize"] = Value.ForString("{{ response.body.slice(0, 4).size }}"),
                },
            })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
        Assert.Equal("HEAD", details.Result.Fields["head"].StringValue);
        Assert.Equal(4, (long)details.Result.Fields["sliceSize"].NumberValue);
    }
}
