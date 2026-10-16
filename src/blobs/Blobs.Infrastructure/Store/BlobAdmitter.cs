using Blobs.Application;
using Blobs.Domain;
using Common.Blobs;
using Common.SeedWork;
using Common.Workflow;
using Microsoft.Extensions.Options;

namespace Blobs.Infrastructure.Store;

/// <summary>
/// <see cref="IBlobAdmission"/>: checking every blob in a new run's input, and attaching the ones that
/// pass — <c>api/docs/binary-data.md</c> § Into a run.
/// <para>
/// Check and attach in one pass, because attaching is what admission <em>is</em>. A blob that was
/// checked and not attached would be a handle the run holds and cannot read, which fails later as
/// <c>UTOS-F103</c> and reads like an access bug rather than like the schedule that should have been
/// refused.
/// </para>
/// </summary>
public sealed class BlobAdmitter(IBlobRepository repository, IOptions<BlobOptions> options)
    : IBlobAdmission
{
    public Task<IReadOnlyList<BlobAdmissionFailure>> AdmitFromClientAsync(
        object? input, BlobUsage arrival, CancellationToken cancellationToken) =>
        AdmitAsync(input, arrival, spawningTree: null, cancellationToken);

    public Task<IReadOnlyList<BlobAdmissionFailure>> AdmitFromSpawnAsync(
        object? input, BlobUsage arrival, ExecutionId spawningTree,
        CancellationToken cancellationToken) =>
        AdmitAsync(input, arrival, spawningTree, cancellationToken);

    /// <summary>
    /// <paramref name="spawningTree"/> null is a client's attach, where a blob must be durable and
    /// needs no relationship to the caller; set, it is a spawn, where the blob must belong to that
    /// tree and need not be durable. Those are the only two ways a blob enters a run from outside, and
    /// the difference between them is one clause.
    /// </summary>
    private async Task<IReadOnlyList<BlobAdmissionFailure>> AdmitAsync(
        object? input, BlobUsage arrival, ExecutionId? spawningTree, CancellationToken cancellationToken)
    {
        var found = BlobWalk.Locate(input).ToList();

        if (found.Count == 0) return [];

        var ids = found
            .Select(entry => entry.Blob.Id)
            .Where(id => id is not null)
            .Select(id => new BlobId(id!))
            .ToList();

        // Two queries for the whole input rather than two per blob — a run started with a hundred
        // handles is a reasonable thing to do. It also keeps the judgement below synchronous and
        // readable, which is worth more than it sounds: the order the reasons are tried in *is* the
        // rule, and an await between two of them would hide that.
        var records = await repository.FindManyAsync(ids, cancellationToken);

        var members = spawningTree is { } tree
            ? await repository.MembersOfRunTreeAsync(ids, tree, cancellationToken)
            : (IReadOnlySet<BlobId>)new HashSet<BlobId>();

        var failures = new List<BlobAdmissionFailure>();
        var attach = new List<BlobId>();

        foreach (var (pointer, blob) in found)
        {
            var reason = Judge(blob, records, members, spawningTree.HasValue, out var id);

            if (reason is { } refused)
                failures.Add(new BlobAdmissionFailure(pointer, refused));
            else if (id is { } accepted)
                attach.Add(accepted);
        }

        // Nothing is attached when anything failed. A refused schedule records no run, so an
        // attachment made on the way to one would name a tree that never existed — collectable only
        // by the sweep's age backstop, weeks later.
        if (failures.Count > 0) return failures;

        var now = DateTimeOffset.UtcNow;

        foreach (var id in attach.Distinct())
        {
            await repository.AttachToRunTreeAsync(id, arrival.RootExecution, now, cancellationToken);
            await repository.RecordEventAsync(
                id, BlobEventKind.Attached, now, arrival, cancellationToken);
        }

        return [];
    }

    /// <summary>
    /// Why this blob cannot be admitted, or null when it can. <paramref name="id"/> is set only for a
    /// stored blob that passed — an inline one has no record and nothing to attach.
    /// </summary>
    private BlobAdmissionReason? Judge(
        WorkflowBlob blob,
        IReadOnlyDictionary<BlobId, StoredBlob> records,
        IReadOnlySet<BlobId> spawningTreeMembers,
        bool fromSpawn,
        out BlobId? id)
    {
        id = null;

        // § A blob as a value: exactly one backing. Both, or neither, is malformed — and "neither" is
        // the shape a client sends when it builds a handle itself, which the spec forbids it from
        // doing.
        if ((blob.Id is null) == (blob.Data is null)) return BlobAdmissionReason.Malformed;

        if (blob.Size < 0) return BlobAdmissionReason.Malformed;

        if (blob.Data is { } inline)
        {
            // An inline blob carries its own bytes, so there is nothing to be offset from and its size
            // is not a claim about anything but itself.
            if (blob.Offset != 0 || blob.Size != inline.Length)
                return BlobAdmissionReason.Malformed;

            // Above the threshold it should have been uploaded. Refused rather than quietly stored on
            // the client's behalf: the daemon would be inventing a durable blob nobody asked for, and
            // the client would have no id for it.
            return inline.Length > options.Value.InlineThresholdBytes
                ? BlobAdmissionReason.TooLarge
                : null;
        }

        if (blob.Offset < 0) return BlobAdmissionReason.Malformed;

        var stored = new BlobId(blob.Id!);

        if (!records.TryGetValue(stored, out var record))
            return BlobAdmissionReason.NotFound;

        // For a spawn, membership is asked *first*, and that order is the rule rather than a
        // preference: everything about a handle the spawning tree does not hold has to be `not_found`,
        // including "it exists but is deleted". Asking after the state check would answer `deleted` for
        // somebody else's object, and a document could probe for ids by spawning runs at them.
        //
        // A client's attach is different by design, and the spec is explicit: it is told `deleted`,
        // `not_ready` and `intermediate` apart, because it needs to know whether to finish an upload,
        // redo one, or carry the blob out of the run that made it.
        if (fromSpawn && !spawningTreeMembers.Contains(stored))
            return BlobAdmissionReason.NotFound;

        var state = record.State switch
        {
            BlobState.Pending => BlobAdmissionReason.NotReady,
            BlobState.Deleted => BlobAdmissionReason.Deleted,
            _ => (BlobAdmissionReason?)null,
        };

        if (state is { } unusable) return unusable;

        // The window has to fit the object. `record.Size` is what the store holds, not what anybody
        // declared, so this cannot be satisfied by a client lying about either number.
        if (blob.Offset + blob.Size > record.Size) return BlobAdmissionReason.OutOfRange;

        // Durability is required of a client's blob and not of a spawn's: an intermediate the spawning
        // tree produced is a value the parent legitimately holds, and handing it to a run the parent
        // starts is passing a value on rather than reaching for somebody else's object.
        if (fromSpawn)
        {
            id = stored;

            return null;
        }

        // A client may attach any durable blob and nothing else. An intermediate belongs to a run that
        // did not carry it out, and the daemon is entitled to remove it — so admitting one would be
        // promising to keep bytes it has already decided nobody asked for.
        if (record.Retention != BlobRetention.Durable) return BlobAdmissionReason.Intermediate;

        id = stored;

        return null;
    }
}
