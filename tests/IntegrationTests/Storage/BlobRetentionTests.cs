using Blobs.Domain;
using Blobs.Infrastructure.Data;
using Blobs.Infrastructure.Store;
using Common.Blobs;
using Common.SeedWork;
using IntegrationTests.Shared.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests.Storage;

/// <summary>
/// When the daemon may remove a blob nobody asked for — <c>api/docs/binary-data.md</c> § Retention.
/// <para>
/// Against the real database, because the rule is a query. It is expressed twice on purpose — once as
/// <see cref="BlobRetentionPolicy"/>, which says what each window means, and once as the statement
/// that finds candidates in one round trip instead of loading every blob the daemon has ever held —
/// and the only thing that can hold the two together is running the statement.
/// </para>
/// <para>
/// Ages are set by moving <c>now</c> rather than by back-dating rows, so nothing here depends on what
/// the clock said when a row was written. Every blob and every run tree is a fresh id, so the table
/// this shares with the rest of the suite cannot make one of these pass or fail.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobRetentionTests(DaemonTestFixture fixture)
{
    private static readonly BlobRetentionPolicy Policy = BlobRetentionPolicy.Default;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset PastTheGrace => Now + Policy.GracePeriod + TimeSpan.FromMinutes(1);

    [Fact]
    public async Task An_intermediate_whose_only_tree_completed_can_go_at_once()
    {
        // Nothing will read it again, so no window applies. The one case where "at once" is literally
        // true — though what acts on it is the next sweep, since deleting bytes on the path of a
        // terminating run would put an object store inside the lifecycle subscription.
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);

        await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Completed, Now, CancellationToken.None);

        Assert.Contains(blob.Id, await Reclaimable(repository, PastTheGrace));
    }

    [Fact]
    public async Task An_intermediate_of_a_tree_that_has_not_ended_stays()
    {
        // The spec's one hard requirement: a blob stays readable while a run can read it — and that
        // includes a retry, which re-runs an activity from its recorded input and so re-reads bytes a
        // later transition no longer mentions.
        var repository = Repository();
        var (blob, _) = await IntermediateAsync(repository);

        Assert.DoesNotContain(blob.Id, await Reclaimable(repository, PastTheGrace));
    }

    [Fact]
    public async Task A_failed_tree_keeps_its_intermediates_for_the_window_and_not_past_it()
    {
        // They are the evidence. Deleting them at the moment of failure is how it is lost.
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);

        await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Failed, Now, CancellationToken.None);

        Assert.DoesNotContain(blob.Id, await Reclaimable(
            repository, Now + Policy.FailedRunWindow - TimeSpan.FromHours(1)));

        Assert.Contains(blob.Id, await Reclaimable(
            repository, Now + Policy.FailedRunWindow + TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_cancelled_tree_is_kept_exactly_as_a_failed_one_is()
    {
        // An operator wants to look at a cancelled run for the same reason as a failed one, and the
        // spec names the two in one breath.
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);

        await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Cancelled, Now, CancellationToken.None);

        Assert.DoesNotContain(blob.Id, await Reclaimable(
            repository, Now + Policy.FailedRunWindow - TimeSpan.FromHours(1)));

        Assert.Contains(blob.Id, await Reclaimable(
            repository, Now + Policy.FailedRunWindow + TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_tree_that_never_ended_is_caught_by_the_backstop()
    {
        // A crash, a lifecycle event that parked, an execution deleted without notice. This is the
        // only rule that can act without an ending, and so the only one that could in principle remove
        // a blob a live run could still read — which is why its window is measured in weeks and is the
        // one a long-waiting workflow has to raise.
        var repository = Repository();
        var (blob, _) = await IntermediateAsync(repository);

        Assert.DoesNotContain(blob.Id, await Reclaimable(
            repository, Now + Policy.InterruptedRunWindow - TimeSpan.FromDays(1)));

        Assert.Contains(blob.Id, await Reclaimable(
            repository, Now + Policy.InterruptedRunWindow + TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task An_upload_that_was_never_completed_goes_once_its_window_passes()
    {
        // Durable by class — somebody asked for it — but there is nothing to keep. The class exists to
        // stop the daemon removing bytes a client is relying on, and a PENDING upload has none.
        var repository = Repository();
        var blob = Blob(BlobState.Pending, BlobRetention.Durable, BlobOrigin.Uploaded);
        await repository.AddAsync(blob, CancellationToken.None);

        Assert.DoesNotContain(blob.Id, await Reclaimable(repository, PastTheGrace));

        Assert.Contains(blob.Id, await Reclaimable(
            repository, Now + Policy.AbandonedUploadWindow + TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task A_durable_blob_is_never_a_candidate_however_old()
    {
        // Requirement 2, and absolute: durable blobs are removed on request and by nothing else.
        var repository = Repository();
        var tree = ExecutionId.New();
        var blob = await AttachAsync(
            repository, Blob(BlobState.Ready, BlobRetention.Durable, BlobOrigin.Produced), tree);

        await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Completed, Now, CancellationToken.None);

        Assert.DoesNotContain(blob.Id, await Reclaimable(repository, Now + TimeSpan.FromDays(3650)));
    }

    [Fact]
    public async Task A_blob_younger_than_the_grace_period_is_not_looked_at()
    {
        // Producing a blob is three writes — the record, the attachment, the history row — so for a few
        // milliseconds a real blob has no tree and is indistinguishable from one nothing can reach. The
        // sweep declines to look at anything this new, rather than the write being widened into a
        // transaction that the attachment's race handling would have to be folded into.
        var repository = Repository();
        var blob = Blob(BlobState.Ready, BlobRetention.Intermediate, BlobOrigin.Produced);
        await repository.AddAsync(blob, CancellationToken.None);

        // Unattached, ready and intermediate: eligible by every other rule there is.
        Assert.DoesNotContain(blob.Id, await Reclaimable(repository, Now));
        Assert.Contains(blob.Id, await Reclaimable(repository, PastTheGrace));
    }

    [Fact]
    public async Task A_blob_two_trees_share_waits_for_both_of_them()
    {
        // The reason membership is a question and not a field. A blob belongs to every tree that
        // produced it or had it attached, and one live tree is enough to keep it.
        var repository = Repository();
        var (blob, first) = await IntermediateAsync(repository);
        var second = ExecutionId.New();
        await repository.AttachToRunTreeAsync(blob.Id, second, Now, CancellationToken.None);

        await repository.RecordRunTreeEndedAsync(
            first, RunTreeOutcome.Completed, Now, CancellationToken.None);

        Assert.DoesNotContain(blob.Id, await Reclaimable(repository, PastTheGrace));

        await repository.RecordRunTreeEndedAsync(
            second, RunTreeOutcome.Completed, Now, CancellationToken.None);

        Assert.Contains(blob.Id, await Reclaimable(repository, PastTheGrace));
    }

    [Fact]
    public async Task A_blob_already_deleted_is_not_a_candidate_again()
    {
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);

        await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Completed, Now, CancellationToken.None);
        await repository.UpdateAsync(blob.Removed(Now), CancellationToken.None);

        Assert.DoesNotContain(blob.Id, await Reclaimable(repository, PastTheGrace));
    }

    [Fact]
    public async Task A_re_delivered_ending_does_not_restart_the_clock()
    {
        // The failed-run window is measured from the ending, and an event store re-delivers. If a
        // second delivery moved the ending forward, the window would silently restart and the evidence
        // would be kept indefinitely — invisibly, because nothing would look wrong.
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);

        Assert.Equal(1, await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Failed, Now, CancellationToken.None));

        Assert.Equal(0, await repository.RecordRunTreeEndedAsync(
            tree, RunTreeOutcome.Failed, Now + Policy.FailedRunWindow, CancellationToken.None));

        Assert.Contains(blob.Id, await Reclaimable(
            repository, Now + Policy.FailedRunWindow + TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task An_ending_for_a_tree_that_touched_no_blob_marks_nothing()
    {
        // This runs for *every* execution, children included, because filtering roots out would mean
        // asking the execution module which executions are roots — the edge the shared lifecycle
        // contract exists to avoid. So the common case has to be one indexed statement that does
        // nothing.
        Assert.Equal(0, await Repository().RecordRunTreeEndedAsync(
            ExecutionId.New(), RunTreeOutcome.Completed, Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_tree_s_blobs_come_back_with_whether_another_live_tree_holds_them()
    {
        var repository = Repository();
        var (shared, tree) = await IntermediateAsync(repository);
        await repository.AttachToRunTreeAsync(shared.Id, ExecutionId.New(), Now, CancellationToken.None);

        var only = await AttachAsync(
            repository, Blob(BlobState.Ready, BlobRetention.Intermediate, BlobOrigin.Produced), tree);

        var found = await repository.FindRunTreeBlobsAsync(tree, CancellationToken.None);

        Assert.True(Held(found, shared.Id).ReachableElsewhere);
        Assert.False(Held(found, only.Id).ReachableElsewhere);
    }

    [Fact]
    public async Task A_tree_whose_other_holder_has_also_ended_is_not_reachable_elsewhere()
    {
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);
        var other = ExecutionId.New();
        await repository.AttachToRunTreeAsync(blob.Id, other, Now, CancellationToken.None);

        await repository.RecordRunTreeEndedAsync(
            other, RunTreeOutcome.Completed, Now, CancellationToken.None);

        var found = await repository.FindRunTreeBlobsAsync(tree, CancellationToken.None);

        Assert.False(Held(found, blob.Id).ReachableElsewhere);
    }

    [Fact]
    public async Task Detaching_a_tree_leaves_the_blob_s_other_trees_alone()
    {
        var repository = Repository();
        var (blob, tree) = await IntermediateAsync(repository);
        var other = ExecutionId.New();
        await repository.AttachToRunTreeAsync(blob.Id, other, Now, CancellationToken.None);

        await repository.DetachRunTreeAsync(tree, CancellationToken.None);

        Assert.False(await repository.BelongsToRunTreeAsync(blob.Id, tree, CancellationToken.None));
        Assert.True(await repository.BelongsToRunTreeAsync(blob.Id, other, CancellationToken.None));
    }

    private static RunTreeBlob Held(IReadOnlyList<RunTreeBlob> found, BlobId id) =>
        Assert.Single(found, entry => entry.Blob.Id == id);

    private static StoredBlob Blob(BlobState state, BlobRetention retention, BlobOrigin origin) =>
        new(BlobId.New(Now), new ObjectKey($"blobs/{Guid.NewGuid():n}"),
            state, retention, origin, "application/octet-stream", 16, Now);

    private static async Task<(StoredBlob Blob, ExecutionId Tree)> IntermediateAsync(
        IBlobRepository repository)
    {
        var tree = ExecutionId.New();

        var blob = await AttachAsync(
            repository, Blob(BlobState.Ready, BlobRetention.Intermediate, BlobOrigin.Produced), tree);

        return (blob, tree);
    }

    private static async Task<StoredBlob> AttachAsync(
        IBlobRepository repository, StoredBlob blob, ExecutionId tree)
    {
        await repository.AddAsync(blob, CancellationToken.None);
        await repository.AttachToRunTreeAsync(blob.Id, tree, Now, CancellationToken.None);

        return blob;
    }

    private static async Task<IReadOnlyList<BlobId>> Reclaimable(
        IBlobRepository repository, DateTimeOffset now)
    {
        // A generous limit: the table holds whatever the rest of the suite left behind, and a short one
        // would make these pass or fail on how many other blobs happen to be older.
        var found = await repository.FindReclaimableAsync(Policy, now, 5000, CancellationToken.None);

        return found.Select(blob => blob.Id).ToList();
    }

    private BlobsDbContext Context() =>
        new(new DbContextOptionsBuilder<BlobsDbContext>()
            .UseNpgsql(fixture.DatabaseConnectionString)
            .Options);

    /// <summary>
    /// A repository on the daemon's own database, through a context this test owns — the same shape
    /// <c>BlobRepositoryTests</c> uses, and for the same reason.
    /// </summary>
    private IBlobRepository Repository()
    {
        var context = Context();
        context.Database.Migrate();

        return new EfBlobRepository(context);
    }
}
