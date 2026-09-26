using System.Text;
using Blobs.Application;
using Blobs.Domain;
using Blobs.Infrastructure.Store;
using Common.Blobs;
using Common.SeedWork;
using Common.Workflow;
using Microsoft.Extensions.Options;

namespace UnitTests.Blobs;

/// <summary>
/// Letting a blob into a run — <c>api/docs/binary-data.md</c> § Into a run, and § Access for a
/// <c>workflow.spawn</c>.
/// <para>
/// The reasons are the contract, which is why they are asserted one at a time. A client that gets
/// <c>intermediate</c> knows to carry the blob out of the run that made it; one that gets
/// <c>too_large</c> knows to upload instead. A daemon that answered <c>not_found</c> to all of them
/// would be conformant in code and useless in practice.
/// </para>
/// </summary>
public class BlobAdmissionTests
{
    private static readonly ExecutionId NewRun = ExecutionId.New();
    private static readonly ExecutionId Parent = ExecutionId.New();

    public class AClientsInput
    {
        [Fact]
        public async Task A_durable_blob_is_admitted_and_attached_to_the_new_run_tree()
        {
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Ready, BlobRetention.Durable, size: 100));

            Assert.Empty(await Admit(repository, Input(WorkflowBlob.Stored(id.Value, 100))));

            // Attaching is what admission *is*. A blob checked and not attached would be a handle the
            // run holds and cannot read, which surfaces later as UTOS-F103 and reads like a bug.
            Assert.Equal([(id, NewRun)], repository.Attached);
            Assert.Equal([(id, BlobEventKind.Attached)], repository.Recorded);
        }

        [Fact]
        public async Task An_intermediate_blob_is_refused_because_nobody_asked_for_it()
        {
            // It belongs to a run that did not carry it out, so the daemon is entitled to remove it.
            // Admitting one would be promising to keep bytes it has already decided nobody wanted.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Ready, BlobRetention.Intermediate, size: 100));

            await Refused(
                repository, Input(WorkflowBlob.Stored(id.Value, 100)),
                "/photo", BlobAdmissionReason.Intermediate);
        }

        [Fact]
        public async Task An_unknown_id_is_not_found_and_nothing_is_attached()
        {
            var repository = new FakeRepository();

            await Refused(
                repository, Input(WorkflowBlob.Stored("b_nothing", 1)),
                "/photo", BlobAdmissionReason.NotFound);

            Assert.Empty(repository.Attached);
        }

        [Theory]
        [InlineData(BlobState.Pending, BlobAdmissionReason.NotReady)]
        [InlineData(BlobState.Deleted, BlobAdmissionReason.Deleted)]
        public async Task A_blob_that_is_not_ready_says_which_it_is(
            BlobState state, BlobAdmissionReason expected)
        {
            // Distinguished on purpose, unlike a *read*, where "deleted" and "not yours" must be
            // indistinguishable. A client attaching its own upload needs to know whether to finish
            // uploading it or to upload it again.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", state, BlobRetention.Durable, size: 100));

            await Refused(
                repository, Input(WorkflowBlob.Stored(id.Value, 100)), "/photo", expected);
        }

        [Fact]
        public async Task A_window_past_the_end_of_the_object_is_out_of_range()
        {
            // Checked against what the store holds, not against what the client declared, so it cannot
            // be satisfied by lying about either number.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Ready, BlobRetention.Durable, size: 100));

            await Refused(
                repository, Input(WorkflowBlob.Stored(id.Value, size: 41, offset: 60)),
                "/photo", BlobAdmissionReason.OutOfRange);
        }

        [Fact]
        public async Task A_window_that_exactly_reaches_the_end_is_admitted()
        {
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Ready, BlobRetention.Durable, size: 100));

            Assert.Empty(await Admit(
                repository, Input(WorkflowBlob.Stored(id.Value, size: 40, offset: 60))));
        }

        [Fact]
        public async Task An_inline_blob_within_the_threshold_needs_no_record_and_no_attachment()
        {
            // Nothing to resolve and nothing to keep: the bytes are the value, so they live and die
            // with whatever carries them.
            var repository = new FakeRepository();

            Assert.Empty(await Admit(
                repository, Input(WorkflowBlob.Inline(Encoding.UTF8.GetBytes("small")))));

            Assert.Empty(repository.Attached);
        }

        [Fact]
        public async Task An_inline_blob_over_the_threshold_is_too_large()
        {
            // Refused rather than quietly stored on the client's behalf: the daemon would be inventing
            // a durable blob nobody asked for, and the client would have no id for it.
            var repository = new FakeRepository();

            await Refused(
                repository, Input(WorkflowBlob.Inline(new byte[Threshold + 1])),
                "/photo", BlobAdmissionReason.TooLarge);
        }

        [Theory]
        [InlineData("neither backing")]
        [InlineData("both backings")]
        [InlineData("inline with an offset")]
        [InlineData("inline whose size is a lie")]
        [InlineData("negative size")]
        [InlineData("negative offset")]
        public async Task An_invariant_of_a_blob_as_a_value_that_does_not_hold_is_malformed(string shape)
        {
            // § A blob as a value. A client must never construct a handle itself, and "neither backing"
            // is exactly the shape of one that tried.
            var blob = shape switch
            {
                "neither backing" => new WorkflowBlob(Size: 3),
                "both backings" => new WorkflowBlob("b_1", [1, 2, 3], Size: 3),
                "inline with an offset" => new WorkflowBlob(Data: [1, 2, 3], Offset: 1, Size: 3),
                "inline whose size is a lie" => new WorkflowBlob(Data: [1, 2, 3], Size: 99),
                "negative size" => new WorkflowBlob("b_1", Size: -1),
                _ => new WorkflowBlob("b_1", Offset: -1, Size: 1),
            };

            await Refused(
                new FakeRepository(), Input(blob), "/photo", BlobAdmissionReason.Malformed);
        }

        [Fact]
        public async Task Every_failing_blob_is_reported_with_its_own_pointer()
        {
            // Fixing one problem must not reveal the next, and a pointer per blob is what makes a
            // refusal actionable rather than a puzzle.
            var repository = new FakeRepository();
            var durable = repository.Add(Record("b_ok", BlobState.Ready, BlobRetention.Durable, size: 8));
            var weak = repository.Add(Record("b_mid", BlobState.Ready, BlobRetention.Intermediate, size: 8));

            var input = new Dictionary<string, object>
            {
                ["fine"] = WorkflowBlob.Stored(durable.Value, 8),
                ["weak"] = WorkflowBlob.Stored(weak.Value, 8),
                ["gone"] = WorkflowBlob.Stored("b_nothing", 8),
                ["nested"] = new Dictionary<string, object?>
                {
                    ["list"] = new List<object?> { WorkflowBlob.Inline(new byte[Threshold + 1]) },
                },
            };

            var failures = await Admit(repository, input);

            // Compared by pointer rather than in order: the order is a map's enumeration order, which is
            // not something a caller should be able to rely on and not something worth pinning.
            Assert.Equal(
                new Dictionary<string, BlobAdmissionReason>
                {
                    ["/weak"] = BlobAdmissionReason.Intermediate,
                    ["/gone"] = BlobAdmissionReason.NotFound,
                    ["/nested/list/0"] = BlobAdmissionReason.TooLarge,
                },
                failures.ToDictionary(failure => failure.Pointer, failure => failure.Reason));

            // And nothing at all is attached. A refused schedule records no run, so an attachment made
            // on the way to one would name a tree that never existed.
            Assert.Empty(repository.Attached);
        }

        [Fact]
        public async Task An_input_with_no_blobs_asks_the_repository_nothing()
        {
            var repository = new FakeRepository();

            Assert.Empty(await Admit(repository, new Dictionary<string, object> { ["n"] = 1 }));

            Assert.Equal(0, repository.Queries);
        }
    }

    public class ASpawnsInput
    {
        [Fact]
        public async Task A_blob_of_the_spawning_tree_is_admitted_even_though_it_is_intermediate()
        {
            // Durability is not required here. An intermediate the parent produced is a value the parent
            // legitimately holds, and handing it to a run the parent starts is passing a value on rather
            // than reaching for somebody else's object.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Ready, BlobRetention.Intermediate, size: 8));
            repository.Member(id, Parent);

            Assert.Empty(await AdmitSpawn(repository, Input(WorkflowBlob.Stored(id.Value, 8))));

            // Attached to the tree it is entering, and it now belongs to both.
            Assert.Equal([(id, NewRun)], repository.Attached);
        }

        [Fact]
        public async Task A_blob_of_another_tree_is_not_found_rather_than_named()
        {
            // The access rule applied to a handle a workflow produced. Reported as `not_found` and not
            // as anything more specific, so a document cannot learn which ids exist by spawning runs at
            // them — the same reasoning that makes every read refusal one code.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Ready, BlobRetention.Durable, size: 8));

            var failures = await AdmitSpawn(repository, Input(WorkflowBlob.Stored(id.Value, 8)));

            Assert.Equal(BlobAdmissionReason.NotFound, Assert.Single(failures).Reason);
            Assert.Empty(repository.Attached);
        }

        [Theory]
        [InlineData(BlobState.Deleted)]
        [InlineData(BlobState.Pending)]
        public async Task A_blob_of_another_tree_is_not_found_whatever_state_it_is_in(BlobState state)
        {
            // Membership is asked *before* the state, and that order is the rule. Asking after it would
            // answer `deleted` for somebody else's object — which is the same leak the read path's single
            // exception exists to prevent, arriving by a different door.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", state, BlobRetention.Durable, size: 8));

            var failures = await AdmitSpawn(repository, Input(WorkflowBlob.Stored(id.Value, 8)));

            Assert.Equal(BlobAdmissionReason.NotFound, Assert.Single(failures).Reason);
        }

        [Fact]
        public async Task A_blob_of_the_spawning_tree_still_has_to_be_readable()
        {
            // Membership first does not mean membership only. The parent's own deleted blob is
            // `deleted`, because there is nothing to hide from a tree that already holds it.
            var repository = new FakeRepository();
            var id = repository.Add(Record("b_1", BlobState.Deleted, BlobRetention.Durable, size: 8));
            repository.Member(id, Parent);

            var failures = await AdmitSpawn(repository, Input(WorkflowBlob.Stored(id.Value, 8)));

            Assert.Equal(BlobAdmissionReason.Deleted, Assert.Single(failures).Reason);
        }

        [Fact]
        public async Task An_inline_blob_needs_no_tree_at_all()
        {
            var repository = new FakeRepository();

            Assert.Empty(await AdmitSpawn(
                repository, Input(WorkflowBlob.Inline(Encoding.UTF8.GetBytes("carried")))));
        }
    }

    public class TheReasonTokens
    {
        [Theory]
        [InlineData(BlobAdmissionReason.NotFound, "not_found")]
        [InlineData(BlobAdmissionReason.NotReady, "not_ready")]
        [InlineData(BlobAdmissionReason.Deleted, "deleted")]
        [InlineData(BlobAdmissionReason.Intermediate, "intermediate")]
        [InlineData(BlobAdmissionReason.OutOfRange, "out_of_range")]
        [InlineData(BlobAdmissionReason.Malformed, "malformed")]
        [InlineData(BlobAdmissionReason.TooLarge, "too_large")]
        public void Are_the_spec_s_and_not_the_member_names(BlobAdmissionReason reason, string expected)
        {
            // These travel as a field violation's `reason`, which is what a client branches on. Pinned
            // as literals so renaming a member cannot change one.
            Assert.Equal(expected, reason.Token());
        }

        [Fact]
        public void The_exception_leads_its_message_with_the_code()
        {
            // The only part of it that survives a Dapr activity boundary, where the exception is
            // flattened to its text and a spawn's failure code has to be recovered from what is left.
            var ex = new BlobsNotAdmittedException(
                [new BlobAdmissionFailure("/photo", BlobAdmissionReason.NotFound)]);

            Assert.StartsWith("UTOS-F104: ", ex.Message);
            Assert.Contains("/photo: not_found", ex.Message);
        }
    }

    private const long Threshold = 4096;

    private static Dictionary<string, object> Input(WorkflowBlob blob) =>
        new() { ["photo"] = blob };

    private static StoredBlob Record(string id, BlobState state, BlobRetention retention, long size) =>
        new(new BlobId(id), new ObjectKey($"blobs/{id}"), state, retention, BlobOrigin.Uploaded,
            "application/octet-stream", size, DateTimeOffset.UnixEpoch);

    private static Task<IReadOnlyList<BlobAdmissionFailure>> Admit(
        FakeRepository repository, Dictionary<string, object> input) =>
        Admitter(repository).AdmitFromClientAsync(input, Arrival, CancellationToken.None);

    private static Task<IReadOnlyList<BlobAdmissionFailure>> AdmitSpawn(
        FakeRepository repository, Dictionary<string, object> input) =>
        Admitter(repository).AdmitFromSpawnAsync(input, Arrival, Parent, CancellationToken.None);

    private static async Task Refused(
        FakeRepository repository,
        Dictionary<string, object> input,
        string pointer,
        BlobAdmissionReason reason)
    {
        var failure = Assert.Single(await Admit(repository, input));

        Assert.Equal(pointer, failure.Pointer);
        Assert.Equal(reason, failure.Reason);
        Assert.Empty(repository.Attached);
    }

    private static BlobUsage Arrival => new(NewRun, NewRun, "acme/thing:1.0.0", "sha256:abc");

    private static IBlobAdmission Admitter(FakeRepository repository) =>
        new BlobAdmitter(
            repository,
            new OptionsWrapper<BlobOptions>(new BlobOptions { InlineThresholdBytes = Threshold }));

    /// <summary>
    /// The records and the memberships, and nothing else — every other method throws, so a rule that
    /// reached for the database in a way these tests do not model would fail rather than pass quietly.
    /// </summary>
    private sealed class FakeRepository : IBlobRepository
    {
        private readonly Dictionary<BlobId, StoredBlob> _records = [];
        private readonly HashSet<(BlobId, ExecutionId)> _members = [];

        public List<(BlobId, ExecutionId)> Attached { get; } = [];

        public List<(BlobId, BlobEventKind)> Recorded { get; } = [];

        /// <summary>How many times the store was asked anything — zero is a meaningful answer.</summary>
        public int Queries { get; private set; }

        public BlobId Add(StoredBlob blob)
        {
            _records[blob.Id] = blob;
            return blob.Id;
        }

        public void Member(BlobId id, ExecutionId tree) => _members.Add((id, tree));

        public Task<IReadOnlyDictionary<BlobId, StoredBlob>> FindManyAsync(
            IReadOnlyCollection<BlobId> ids, CancellationToken cancellationToken)
        {
            Queries++;

            return Task.FromResult<IReadOnlyDictionary<BlobId, StoredBlob>>(
                ids.Where(_records.ContainsKey).Distinct().ToDictionary(id => id, id => _records[id]));
        }

        public Task<IReadOnlySet<BlobId>> MembersOfRunTreeAsync(
            IReadOnlyCollection<BlobId> ids, ExecutionId rootExecution, CancellationToken cancellationToken)
        {
            Queries++;

            return Task.FromResult<IReadOnlySet<BlobId>>(
                ids.Where(id => _members.Contains((id, rootExecution))).ToHashSet());
        }

        public Task AttachToRunTreeAsync(
            BlobId id, ExecutionId rootExecution, CancellationToken cancellationToken)
        {
            Attached.Add((id, rootExecution));
            _members.Add((id, rootExecution));

            return Task.CompletedTask;
        }

        public Task RecordEventAsync(
            BlobId id, BlobEventKind kind, DateTimeOffset at, BlobUsage? usage,
            CancellationToken cancellationToken)
        {
            Recorded.Add((id, kind));

            return Task.CompletedTask;
        }

        public Task<StoredBlob?> FindAsync(BlobId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(StoredBlob blob, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateAsync(StoredBlob blob, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> BelongsToRunTreeAsync(
            BlobId id, ExecutionId rootExecution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BlobHistoryEntry>> HistoryAsync(
            BlobId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RecordRunTreeEndedAsync(
            ExecutionId rootExecution, RunTreeOutcome outcome, DateTimeOffset at,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredBlob>> FindReclaimableAsync(
            BlobRetentionPolicy policy, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RunTreeBlob>> FindRunTreeBlobsAsync(
            ExecutionId rootExecution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DetachRunTreeAsync(ExecutionId rootExecution, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlobSummary?> SummaryAsync(BlobId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlobPage> ListAsync(
            BlobQuery query, int pageSize, string pageToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsHeldByALiveRunAsync(BlobId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<BlobId>> HeldByALiveRunAsync(
            IReadOnlyCollection<BlobId> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
