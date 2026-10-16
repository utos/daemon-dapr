using Blobs.Domain;
using Blobs.Infrastructure.Data;
using Blobs.Infrastructure.Store;
using Common.Blobs;
using Common.SeedWork;
using IntegrationTests.Shared.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests.Storage;

/// <summary>
/// The daemon's record of what blobs exist and who may read them, against the real database.
/// <para>
/// Against Postgres rather than an in-memory provider, because the two behaviours worth having are
/// enforced by the schema and not by the code: attaching a blob to a run tree twice is a no-op
/// because the pair is the primary key, and recording the same event twice folds into one row
/// because of a unique index. An in-memory provider would let both of those pass while the real
/// database rejected them, or the reverse.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobRepositoryTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task A_blob_round_trips_through_the_record()
    {
        var repository = Repository();
        var blob = Pending();

        await repository.AddAsync(blob, CancellationToken.None);

        var found = await repository.FindAsync(blob.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(blob.Id, found.Id);
        Assert.Equal(BlobState.Pending, found.State);
        Assert.Equal(BlobRetention.Durable, found.Retention);
        Assert.Equal(BlobOrigin.Uploaded, found.Origin);
        Assert.Equal("image/png", found.MediaType);
        Assert.Equal("report.png", found.Name);
    }

    [Fact]
    public async Task An_unknown_id_is_an_answer_not_a_failure()
    {
        // A forged id gets a failure, not bytes. That it resolves to "no such blob" rather than
        // throwing is what lets the caller report UTOS-F103 the same way it does for a deleted one.
        Assert.Null(await Repository().FindAsync(
            new BlobId("b_nothing"), CancellationToken.None));
    }

    [Fact]
    public async Task Completing_an_upload_takes_the_size_the_store_reports()
    {
        var repository = Repository();
        var blob = Pending();
        await repository.AddAsync(blob, CancellationToken.None);

        await repository.UpdateAsync(
            blob.Completed(9182, DateTimeOffset.UnixEpoch), CancellationToken.None);

        var found = await repository.FindAsync(blob.Id, CancellationToken.None);

        Assert.Equal(BlobState.Ready, found!.State);
        Assert.Equal(9182, found.Size);
    }

    [Fact]
    public async Task Promotion_is_irreversible_and_moves_no_bytes()
    {
        var repository = Repository();
        var blob = Produced();
        await repository.AddAsync(blob, CancellationToken.None);

        var promoted = (await repository.FindAsync(blob.Id, CancellationToken.None))!.Promoted();
        await repository.UpdateAsync(promoted, CancellationToken.None);

        var found = await repository.FindAsync(blob.Id, CancellationToken.None);

        Assert.Equal(BlobRetention.Durable, found!.Retention);

        // The object key never moves: a blob's class cannot be known when it is written, so a
        // design that kept the classes apart would make this a server-side copy of something that
        // may be gigabytes.
        Assert.Equal(blob.Key, found.Key);
    }

    [Fact]
    public async Task A_blob_is_readable_only_by_the_run_tree_it_belongs_to()
    {
        // The whole of the spec's access rule. Knowing an id grants nothing.
        var repository = Repository();
        var blob = Produced();
        await repository.AddAsync(blob, CancellationToken.None);

        var mine = new ExecutionId(Guid.NewGuid());
        var theirs = new ExecutionId(Guid.NewGuid());

        await repository.AttachToRunTreeAsync(blob.Id, mine, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(await repository.BelongsToRunTreeAsync(blob.Id, mine, CancellationToken.None));
        Assert.False(await repository.BelongsToRunTreeAsync(blob.Id, theirs, CancellationToken.None));
    }

    [Fact]
    public async Task Attaching_twice_is_a_no_op()
    {
        // Two activities of one tree can both attach the same blob. The pair being the primary key
        // is what makes exactly one insert win, rather than a check-then-insert both would pass.
        var repository = Repository();
        var blob = Produced();
        await repository.AddAsync(blob, CancellationToken.None);

        var tree = new ExecutionId(Guid.NewGuid());

        await repository.AttachToRunTreeAsync(blob.Id, tree, DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.AttachToRunTreeAsync(blob.Id, tree, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(await repository.BelongsToRunTreeAsync(blob.Id, tree, CancellationToken.None));
    }

    [Fact]
    public async Task Fan_out_collapses_into_one_history_row()
    {
        // The property the aggregation exists for: a loop calling five hundred sub-workflows that
        // each read one blob is one row, because the row is keyed by the *root* execution and the
        // workflow rather than by whoever did the reading. Row count is bounded by how many
        // distinct documents touched the blob, which is small, rather than by fan-out, which is not.
        var repository = Repository();
        var blob = Produced();
        await repository.AddAsync(blob, CancellationToken.None);

        var root = new ExecutionId(Guid.NewGuid());

        for (var i = 0; i < 500; i++)
        {
            await repository.RecordEventAsync(
                blob.Id, BlobEventKind.Read, DateTimeOffset.UtcNow,
                new BlobUsage(new ExecutionId(Guid.NewGuid()), root, "acme/ingest:1.0.0", "sha256:abc", "fetch"),
                CancellationToken.None);
        }

        var history = await repository.HistoryAsync(blob.Id, CancellationToken.None);

        var row = Assert.Single(history);
        Assert.Equal(BlobEventKind.Read, row.Kind);
        Assert.Equal(500, row.Executions);
        Assert.Equal("acme/ingest:1.0.0", row.Workflow);
        Assert.Equal("sha256:abc", row.WorkflowDigest);
    }

    [Fact]
    public async Task Different_workflows_and_kinds_are_different_rows()
    {
        var repository = Repository();
        var blob = Produced();
        await repository.AddAsync(blob, CancellationToken.None);

        var root = new ExecutionId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        await repository.RecordEventAsync(blob.Id, BlobEventKind.Produced, now,
            new BlobUsage(root, root, "acme/fetch:1.0.0"), CancellationToken.None);

        await repository.RecordEventAsync(blob.Id, BlobEventKind.Read, now,
            new BlobUsage(root, root, "acme/fetch:1.0.0"), CancellationToken.None);

        await repository.RecordEventAsync(blob.Id, BlobEventKind.Read, now,
            new BlobUsage(root, root, "acme/other:1.0.0"), CancellationToken.None);

        // A download involves no execution and is its own row.
        await repository.RecordEventAsync(
            blob.Id, BlobEventKind.Downloaded, now, null, CancellationToken.None);

        Assert.Equal(4, (await repository.HistoryAsync(blob.Id, CancellationToken.None)).Count);
    }

    private static StoredBlob Pending() =>
        new(BlobId.New(DateTimeOffset.UtcNow),
            new ObjectKey("test/" + Guid.NewGuid().ToString("n")),
            BlobState.Pending,
            BlobRetention.Durable,
            BlobOrigin.Uploaded,
            "image/png",
            0,
            DateTimeOffset.UtcNow,
            "report.png");

    private static StoredBlob Produced() =>
        new(BlobId.New(DateTimeOffset.UtcNow),
            new ObjectKey("test/" + Guid.NewGuid().ToString("n")),
            BlobState.Ready,
            BlobRetention.Intermediate,
            BlobOrigin.Produced,
            "application/json",
            42,
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_daemon_migrates_the_blob_tables_on_the_way_up()
    {
        // The daemon-side half, asserted on its own so a repository test failing means the
        // repository is wrong and not that the schema was never created. Waited for rather than
        // read once, for the reason the bucket check is: migrations run during startup, and Aspire
        // reports the daemon Running when Kestrel answers, which is not ordered against them.
        await using var context = Context();
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (await TablesExist(context)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }

        Assert.Fail(
            "the daemon never created the blob tables, so UseBlobsModule did not run its "
            + "migrations — check that AddBlobsModule is composed and the context resolves");
    }

    private static async Task<bool> TablesExist(BlobsDbContext context)
    {
        var applied = await context.Database.GetAppliedMigrationsAsync(CancellationToken.None);
        return applied.Any();
    }

    private BlobsDbContext Context() =>
        new(new DbContextOptionsBuilder<BlobsDbContext>()
            .UseNpgsql(fixture.DatabaseConnectionString)
            .Options);

    /// <summary>
    /// A repository on the daemon's own database, through a context this test owns.
    /// <para>
    /// The schema is ensured here rather than borrowed from the daemon's startup. These are tests
    /// of the repository, and depending on another process's provisioning would make every one of
    /// them fail for a reason none of them is about — see
    /// <c>The_daemon_migrates_the_blob_tables_on_the_way_up</c>, which is where that fact belongs.
    /// </para>
    /// </summary>
    private IBlobRepository Repository()
    {
        var context = Context();
        context.Database.Migrate();

        return new EfBlobRepository(context);
    }
}
