using System.Text;
using Blobs.Application;
using Blobs.Domain;
using Blobs.Infrastructure.Data;
using Blobs.Infrastructure.Store;
using Common.Blobs;
using Common.Blobs.S3;
using Common.Constants;
using Common.SeedWork;
using IntegrationTests.Shared.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IntegrationTests.Storage;

/// <summary>
/// Removing the bytes, against a real object store — § Retention, and the half that actually frees
/// space.
/// <para>
/// <see cref="BlobRetentionTests"/> asks which blobs may go; this asks what happens to one that may.
/// The ordering is the part worth a test: the bytes go first and the record second, so an interruption
/// leaves a record that says <c>READY</c> for an object that is gone — a read fails <c>UTOS-F103</c>
/// and the next pass finishes the job, because deleting an object that is not there is a success. The
/// other order leaves an object nothing remembers, which no pass can ever find.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobSweepTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task The_sweep_removes_the_bytes_and_leaves_the_record_answerable()
    {
        // Requirement 3: a removed blob stays answerable. GetBlob answers DELETED for an id the daemon
        // issued, so reading an old run's result looks like cleanup rather than corruption.
        var (repository, store, reclaimer) = Module();
        var (blob, tree) = await StoredIntermediate(repository, store);

        await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Completed, DateTimeOffset.UtcNow, CancellationToken.None);

        var report = await reclaimer.SweepAsync(CancellationToken.None);

        Assert.True(report.Deleted >= 1);

        // Asserted by key rather than by the report's count. A sweep is allowed to collect whatever
        // else the suite left behind — that is what it is for — so what this test owns is its own blob.
        Assert.Null(await store.StatAsync(blob.Key, CancellationToken.None));

        var record = await Record(repository, blob.Id);
        Assert.Equal(BlobState.Deleted, record.State);
        Assert.False(record.IsReadable);
    }

    [Fact]
    public async Task The_sweep_leaves_a_blob_whose_tree_is_still_running()
    {
        var (repository, store, reclaimer) = Module();
        var (blob, _) = await StoredIntermediate(repository, store);

        await reclaimer.SweepAsync(CancellationToken.None);

        Assert.NotNull(await store.StatAsync(blob.Key, CancellationToken.None));
        Assert.Equal(BlobState.Ready, (await Record(repository, blob.Id)).State);
    }

    [Fact]
    public async Task Deleting_an_execution_removes_the_tree_s_intermediates()
    {
        var (repository, store, reclaimer) = Module();
        var (blob, tree) = await StoredIntermediate(repository, store);

        var report = await reclaimer.ReclaimRunTreeAsync(tree, deleteDurable: false, CancellationToken.None);

        Assert.Equal(new BlobReclamationReport(1, 0, 0), report);
        Assert.Null(await store.StatAsync(blob.Key, CancellationToken.None));

        // And the attachment is gone with the execution that named it.
        Assert.False(await repository.BelongsToRunTreeAsync(blob.Id, tree, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_an_execution_keeps_a_durable_blob_unless_asked()
    {
        var (repository, store, reclaimer) = Module();
        var (blob, tree) = await StoredIntermediate(repository, store);
        await repository.UpdateAsync(blob.Promoted(), CancellationToken.None);

        Assert.Equal(
            new BlobReclamationReport(0, 1, 0),
            await reclaimer.ReclaimRunTreeAsync(tree, deleteDurable: false, CancellationToken.None));

        Assert.NotNull(await store.StatAsync(blob.Key, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_an_execution_with_delete_blobs_removes_what_the_run_produced()
    {
        var (repository, store, reclaimer) = Module();
        var (blob, tree) = await StoredIntermediate(repository, store);
        await repository.UpdateAsync(blob.Promoted(), CancellationToken.None);

        Assert.Equal(
            new BlobReclamationReport(1, 0, 0),
            await reclaimer.ReclaimRunTreeAsync(tree, deleteDurable: true, CancellationToken.None));

        Assert.Null(await store.StatAsync(blob.Key, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_blobs_never_removes_a_client_s_upload()
    {
        // It may have started other runs, and deleting it belongs to DeleteBlob. Origin is metadata
        // rather than a retention class everywhere else in the spec; this is the one place it decides
        // something, and what it decides is whose object it is.
        var (repository, store, reclaimer) = Module();
        var tree = ExecutionId.New();

        var upload = await Stored(
            repository, store, tree, BlobRetention.Durable, BlobOrigin.Uploaded);

        Assert.Equal(
            new BlobReclamationReport(0, 1, 0),
            await reclaimer.ReclaimRunTreeAsync(tree, deleteDurable: true, CancellationToken.None));

        Assert.NotNull(await store.StatAsync(upload.Key, CancellationToken.None));
    }

    [Fact]
    public async Task A_blob_a_live_tree_still_holds_is_skipped_rather_than_deleted()
    {
        var (repository, store, reclaimer) = Module();
        var (blob, tree) = await StoredIntermediate(repository, store);
        await repository.AttachToRunTreeAsync(blob.Id, ExecutionId.New(), CancellationToken.None);

        Assert.Equal(
            new BlobReclamationReport(0, 1, 0),
            await reclaimer.ReclaimRunTreeAsync(tree, deleteDurable: true, CancellationToken.None));

        Assert.NotNull(await store.StatAsync(blob.Key, CancellationToken.None));
        Assert.Equal(BlobState.Ready, (await Record(repository, blob.Id)).State);
    }

    private static async Task<StoredBlob> Record(IBlobRepository repository, BlobId id) =>
        await repository.FindAsync(id, CancellationToken.None)
        ?? throw new InvalidOperationException($"no record of blob '{id}'");

    private static Task<(StoredBlob Blob, ExecutionId Tree)> StoredIntermediate(
        IBlobRepository repository, IObjectStore store) =>
        StoredIntermediateFor(repository, store, ExecutionId.New());

    private static async Task<(StoredBlob, ExecutionId)> StoredIntermediateFor(
        IBlobRepository repository, IObjectStore store, ExecutionId tree) =>
        (await Stored(repository, store, tree, BlobRetention.Intermediate, BlobOrigin.Produced), tree);

    /// <summary>Real bytes in the real store, and a record that resolves to them.</summary>
    private static async Task<StoredBlob> Stored(
        IBlobRepository repository,
        IObjectStore store,
        ExecutionId tree,
        BlobRetention retention,
        BlobOrigin origin)
    {
        var now = DateTimeOffset.UtcNow;
        var id = BlobId.New(now);
        var key = new ObjectKey($"blobs/{id.Value}");

        var stat = await store.WriteAsync(
            key, new MemoryStream(Encoding.UTF8.GetBytes("bytes to be reclaimed")),
            "text/plain", CancellationToken.None);

        var blob = new StoredBlob(
            id, key, BlobState.Ready, retention, origin, "text/plain", stat.Size, now);

        await repository.AddAsync(blob, CancellationToken.None);
        await repository.AttachToRunTreeAsync(id, tree, CancellationToken.None);

        return blob;
    }

    /// <summary>
    /// The reclaimer over the real database and the real store.
    /// <para>
    /// The grace period is zero here, and nowhere else. It exists so the sweep does not look at a blob
    /// that was written milliseconds ago and has not been attached yet; a test that wanted to observe
    /// a sweep at all would otherwise have to wait ten minutes. Everything the grace period protects
    /// against is asserted in <see cref="BlobRetentionTests"/>, where <c>now</c> is a parameter.
    /// </para>
    /// </summary>
    private (IBlobRepository Repository, IObjectStore Store, IBlobReclamation Reclaimer) Module()
    {
        var context = new BlobsDbContext(
            new DbContextOptionsBuilder<BlobsDbContext>()
                .UseNpgsql(fixture.DatabaseConnectionString)
                .Options);

        context.Database.Migrate();

        var repository = new EfBlobRepository(context);
        var store = Store();

        var options = new OptionsWrapper<BlobOptions>(new BlobOptions
        {
            SweepGracePeriod = TimeSpan.Zero,
        });

        return (repository, store,
            new BlobReclaimer(repository, options, NullLogger<BlobReclaimer>.Instance, store));
    }

    private IObjectStore Store() => TestBlobStore.For(fixture);
}
