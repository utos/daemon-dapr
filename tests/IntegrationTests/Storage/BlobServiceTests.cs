using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Storage;

/// <summary>
/// <c>BlobService</c> — <c>api/docs/binary-data.md</c> § The BlobService, the inspect-and-remove half.
/// <para>
/// Through the real service against real MinIO, because what these prove is the surface: the enums exist
/// on both sides of the wire with different members and a different zero value, the timestamps are
/// optional, and a signed location is only useful if a client can actually replay it. A test below does
/// exactly that — downloads a blob's bytes with nothing but the URL and headers the daemon handed over,
/// which is the one assertion that says the daemon is out of the data path rather than merely claiming
/// to be.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobServiceTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task A_blob_a_run_returned_is_described_by_its_id()
    {
        var (id, body, _) = await Produced("described");

        var found = await fixture.BlobClient.GetBlobAsync(new GetBlobRequest { Id = id });

        var blob = found.Blob;

        Assert.Equal(id, blob.Id);
        Assert.Equal(BlobState.Ready, blob.State);

        // Durable, because the run returned it — which is the line § Retention draws, and it is about
        // whether somebody asked for the blob rather than about where it came from.
        Assert.Equal(BlobRetention.Durable, blob.Retention);
        Assert.Equal(BlobOrigin.Produced, blob.Origin);
        Assert.Equal(body.Length, blob.Size);
        Assert.Equal("text/plain", blob.MediaType);
        Assert.NotNull(blob.CompletedAt);
        Assert.Null(blob.DeletedAt);

        // The producer, as data rather than as a reference: a durable blob outlives the execution that
        // made it, and deleting that execution must not erase why the blob exists.
        Assert.NotNull(blob.Producer);
        Assert.NotEmpty(blob.Producer.ExecutionId);
        Assert.Equal(blob.Producer.ExecutionId, blob.Producer.RootExecutionId);
        Assert.Equal("described", blob.Producer.Workflow.Name);
        Assert.Equal("fetch", blob.Producer.Activity);

        // The handle a client places in the next run's input — built by the daemon, because a client
        // must never construct one.
        Assert.Equal(id, found.Handle.Id);
        Assert.Equal(body.Length, found.Handle.Size);
        Assert.Equal(0, found.Handle.Offset);
        Assert.True(found.Handle.Data.IsEmpty);
    }

    [Fact]
    public async Task A_signed_download_hands_the_bytes_over_without_the_daemon_in_the_path()
    {
        // The whole reason the service exists in this shape. Nothing below goes through the daemon: the
        // URL, the method and the headers are replayed exactly as given, straight at the store.
        var (id, body, _) = await Produced("signed-download");

        var found = await fixture.BlobClient.GetBlobAsync(
            new GetBlobRequest { Id = id, IncludeDownload = true });

        Assert.NotNull(found.Download);
        Assert.Equal("GET", found.Download.Method);
        Assert.True(found.Download.ExpiresAt.ToDateTimeOffset() > DateTimeOffset.UtcNow);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(
            new HttpMethod(found.Download.Method), found.Download.Url);

        foreach (var (name, value) in found.Download.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode,
            $"the signed location answered {(int)response.StatusCode}");

        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Being_issued_a_download_is_recorded_as_a_use()
    {
        // An operator asking why a blob is still around should see that somebody fetched it.
        var (id, _, _) = await Produced("download-recorded");

        await fixture.BlobClient.GetBlobAsync(
            new GetBlobRequest { Id = id, IncludeDownload = true });

        var history = await fixture.BlobClient.GetBlobHistoryAsync(
            new GetBlobHistoryRequest { Id = id });

        Assert.Contains(history.Events, e => e.Kind == BlobEventKind.Downloaded);
    }

    [Fact]
    public async Task A_history_says_what_produced_the_blob_and_what_used_it()
    {
        // Rows are about the handle, never about bytes, and aggregated by (event, root execution,
        // workflow) — so a run that reads a blob in a loop is one row with `executions` counting it.
        var (id, _, _) = await Produced("history");

        var history = await fixture.BlobClient.GetBlobHistoryAsync(
            new GetBlobHistoryRequest { Id = id });

        Assert.Equal(id, history.Blob.Id);

        var produced = Assert.Single(history.Events, e => e.Kind == BlobEventKind.Produced);

        Assert.Equal("history", produced.Workflow.Name);
        Assert.Equal("1.0.0", produced.Workflow.Version);
        Assert.Equal("fetch", produced.Activity);
        Assert.NotEmpty(produced.RootExecutionId);
        Assert.Equal(1, produced.Executions);

        // Oldest first, by `first_at`.
        Assert.Equal(
            history.Events.OrderBy(e => e.FirstAt.ToDateTimeOffset()).Select(e => e.Kind),
            history.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task An_id_the_daemon_never_issued_is_not_found()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.BlobClient.GetBlobAsync(new GetBlobRequest { Id = "b_nothing" }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task A_listing_filters_and_pages()
    {
        var (id, _, _) = await Produced("listed");

        // One blob at a time, so the paging is exercised rather than merely present. The blob this test
        // made is somewhere in the pages; what matters is that walking them reaches it exactly once and
        // terminates.
        var seen = new List<string>();
        var token = string.Empty;

        for (var page = 0; page < 200; page++)
        {
            var listed = await fixture.BlobClient.ListBlobsAsync(new ListBlobsRequest
            {
                Retention = BlobRetention.Durable,
                State = BlobState.Ready,
                PageSize = 1,
                PageToken = token,
            });

            seen.AddRange(listed.Blobs.Select(blob => blob.Id));

            // Every blob on every page matches the filter, which is what a filter means.
            Assert.All(listed.Blobs, blob =>
            {
                Assert.Equal(BlobRetention.Durable, blob.Retention);
                Assert.Equal(BlobState.Ready, blob.State);
            });

            if (string.IsNullOrEmpty(listed.NextPageToken)) break;

            token = listed.NextPageToken;
        }

        Assert.Single(seen, found => found == id);

        // Newest first, which descending id gives for free: an id is time-ordered at the front.
        Assert.Equal(seen.OrderByDescending(found => found, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task A_listing_can_ask_for_one_run_tree()
    {
        var (id, _, execution) = await Produced("by-tree");

        var listed = await fixture.BlobClient.ListBlobsAsync(
            new ListBlobsRequest { RootExecutionId = execution });

        Assert.Equal(id, Assert.Single(listed.Blobs).Id);
    }

    [Fact]
    public async Task Deleting_a_blob_removes_the_bytes_and_keeps_the_record_answerable()
    {
        // Requirement 3: a removed blob stays answerable, so a handle in an old result reads as cleanup
        // rather than as corruption.
        var (id, _, _) = await Produced("deleted");

        var deleted = await fixture.BlobClient.DeleteBlobAsync(new DeleteBlobRequest { Id = id });

        Assert.Equal(BlobState.Deleted, deleted.Blob.State);
        Assert.NotNull(deleted.Blob.DeletedAt);

        var found = await fixture.BlobClient.GetBlobAsync(new GetBlobRequest { Id = id });

        Assert.Equal(BlobState.Deleted, found.Blob.State);

        // No handle for a blob whose bytes are gone: there is nothing to put in a run's input.
        Assert.Null(found.Handle);
        Assert.Null(found.Download);
    }

    [Fact]
    public async Task Deleting_a_blob_twice_is_the_same_answer()
    {
        var (id, _, _) = await Produced("deleted-twice");

        await fixture.BlobClient.DeleteBlobAsync(new DeleteBlobRequest { Id = id });

        var again = await fixture.BlobClient.DeleteBlobAsync(new DeleteBlobRequest { Id = id });

        Assert.Equal(BlobState.Deleted, again.Blob.State);
    }

    [Fact]
    public async Task An_unfiltered_prune_is_refused()
    {
        // The guard against the request nobody means to send: a mistyped flag should do nothing, not
        // empty the bucket.
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.BlobClient.PruneBlobsAsync(new PruneBlobsRequest()).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task A_dry_run_prune_counts_and_removes_nothing()
    {
        var (id, body, _) = await Produced("dry-pruned");

        var dry = await fixture.BlobClient.PruneBlobsAsync(new PruneBlobsRequest
        {
            Retention = BlobRetention.Durable,
            CreatedBefore = DateTimeOffset.UtcNow.AddMinutes(1).ToTimestamp(),
            DryRun = true,
        });

        Assert.True(dry.Count >= 1);
        Assert.True(dry.Bytes >= body.Length);

        // Still there, which is the whole point of a dry run.
        var found = await fixture.BlobClient.GetBlobAsync(new GetBlobRequest { Id = id });
        Assert.Equal(BlobState.Ready, found.Blob.State);
    }

    /// <summary>
    /// Runs a workflow that fetches a body over the inline threshold, so the blob is stored and has a
    /// record. <paramref name="carryOut"/> returns it, which promotes it to durable — the only class a
    /// client may attach, and the one these tests want.
    /// </summary>
    private async Task<(string Id, string Body, string Execution)> Produced(
        string name, bool carryOut = true)
    {
        var body = new string('b', 400 * 1024) + name;
        var path = $"/blob-service/{name}";

        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(body));

        var result = carryOut
            ? new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } }
            : new Struct { Fields = { ["size"] = Value.ForString("{{ response.body.size }}") } };

        var request = new TestWorkflowBuilder(name, fixture.MockBaseUrl)
            .HttpGet("fetch", path)
            .OnResult(result)
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);

        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        return (details.Result.Fields["photo"].BlobValue.Id, body, scheduled.Id);
    }
}
