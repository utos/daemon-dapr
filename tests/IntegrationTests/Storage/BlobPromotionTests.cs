using Blobs.Domain;
using BlobOrigin = Blobs.Domain.BlobOrigin;
using BlobRetention = Blobs.Domain.BlobRetention;
using BlobState = Blobs.Domain.BlobState;
using Blobs.Infrastructure.Data;
using Blobs.Infrastructure.Store;
using Common.SeedWork;
using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Microsoft.EntityFrameworkCore;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Storage;

/// <summary>
/// A blob that reaches a root execution's output stream becomes durable —
/// <c>api/docs/binary-data.md</c> § Out of a run.
/// <para>
/// This is the line between the two retention classes, and it is not about where a blob came from. A
/// run that fetches five things and returns one made four nobody asked for; those four are what fill
/// a bucket without anyone noticing, and the one is what a person will come back for. So the whole
/// question is whether a blob left the run, and these tests are a real run leaving it or not.
/// </para>
/// <para>
/// Through the real thing end to end, because promotion depends on a chain that is invisible at every
/// link: the value has to still be a blob after the transition, the walk has to find it, the append
/// has to know it is on a root's stream, and the access check has to pass. Any of those failing leaves
/// a run that completes and a blob that is silently swept later.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobPromotionTests(DaemonTestFixture fixture)
{
    /// <summary>Over the inline threshold, so the blob is stored and has a record to promote.</summary>
    private static string LargeBody => new('z', 400 * 1024);

    [Fact]
    public async Task A_blob_a_run_returns_becomes_durable()
    {
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promote-result").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(LargeBody));

        var request = new TestWorkflowBuilder("promoted-result", fixture.MockBaseUrl)
            .HttpGet("fetch", "/promote-result")
            .OnResult(new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        var id = details.Result.Fields["photo"].BlobValue.Id;
        Assert.NotEmpty(id);

        var record = await Record(new BlobId(id));

        Assert.Equal(BlobRetention.Durable, record.Retention);

        // Where it came from is metadata and not the class. An upload and a returned result are both
        // things a person will look for later, and both are removed the same way.
        Assert.Equal(BlobOrigin.Produced, record.Origin);
        Assert.Equal(BlobState.Ready, record.State);
    }

    [Fact]
    public async Task A_blob_a_run_fetched_and_did_not_carry_out_stays_intermediate()
    {
        // The other four of the five. This run reads the body's *size* — so the bytes were fetched,
        // stored and used — and returns a number. Nobody asked for the blob, so nothing keeps it.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promote-nothing").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(LargeBody));

        var request = new TestWorkflowBuilder("unpromoted-result", fixture.MockBaseUrl)
            .HttpGet("fetch", "/promote-nothing")
            .OnResult(new Struct { Fields = { ["size"] = Value.ForString("{{ response.body.size }}") } })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        // Found through the run tree rather than through the result, which is the point: nothing the
        // client can see names this blob.
        var produced = await RunTreeBlobs(scheduled.Id);

        Assert.Equal(BlobRetention.Intermediate, Assert.Single(produced).Blob.Retention);
    }

    [Fact]
    public async Task A_blob_a_run_emits_becomes_durable_when_the_entry_is_appended()
    {
        // § Out of a run: a blob reaches the stream as an emitted value when the value is appended, or
        // in the result when the run returns. Emit-and-continue, so this one is durable while the run
        // is still going.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promote-emit").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(LargeBody));

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/promote-emit-done").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        var request = new TestWorkflowBuilder("promoted-emission", fixture.MockBaseUrl)
            .HttpGet("fetch", "/promote-emit")
            .OnEmit(new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } }, "finish")
            .HttpGet("finish", "/promote-emit-done")
            .OnResult(new Struct { Fields = { ["done"] = Value.ForString("yes") } })
            .Build("fetch");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var produced = await RunTreeBlobs(scheduled.Id);

        Assert.Equal(BlobRetention.Durable, Assert.Single(produced).Blob.Retention);
    }

    private async Task<StoredBlob> Record(BlobId id) =>
        await Repository().FindAsync(id, CancellationToken.None)
        ?? throw new InvalidOperationException($"the daemon kept no record of blob '{id}'");

    private async Task<IReadOnlyList<RunTreeBlob>> RunTreeBlobs(string executionId) =>
        await Repository().FindRunTreeBlobsAsync(
            ExecutionId.Parse(executionId), CancellationToken.None);

    private IBlobRepository Repository() =>
        new EfBlobRepository(new BlobsDbContext(
            new DbContextOptionsBuilder<BlobsDbContext>()
                .UseNpgsql(fixture.DatabaseConnectionString)
                .Options));
}
