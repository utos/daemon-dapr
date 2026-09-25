using Blobs.Domain;
using Common.Blobs;
using Common.SeedWork;
using Common.Workflow;
using Microsoft.Extensions.Options;

namespace Blobs.Infrastructure.Store;

/// <summary>
/// <see cref="IBlobAccess"/>: the blobs module's answer to what a run does with blobs.
/// <para>
/// The two halves the object store cannot provide on its own. Producing mints an id, writes the
/// bytes, and records both the run tree the blob belongs to and the provenance that made it —
/// because an id is not an address, and a blob nobody can resolve is a blob nobody can read.
/// Reading resolves an id to a key and enforces the access rule, which is the only place it is
/// enforced.
/// </para>
/// </summary>
public sealed class BlobAccess(
    IBlobRepository repository,
    IOptions<BlobOptions> options,
    IObjectStore? store = null) : IBlobAccess
{
    public long InlineThreshold => options.Value.InlineThresholdBytes;

    public bool CanStore => store is not null;

    public async Task<WorkflowBlob> ProduceAsync(
        Stream content,
        string mediaType,
        string? name,
        BlobUsage provenance,
        CancellationToken cancellationToken)
    {
        if (store is null)
            throw new BlobStoreUnavailableException(
                "This blob must be stored and no blob store is configured.");

        var now = DateTimeOffset.UtcNow;
        var id = BlobId.New(now);

        // The key is derived from the id, but an id is still not an address: a client never sees a
        // key, and a daemon resolves an id by looking it up rather than by computing one. The
        // derivation is a convenience for operators reading a bucket, not an addressing scheme.
        var key = new ObjectKey($"blobs/{id.Value}");

        var stat = await store.WriteAsync(key, content, mediaType, cancellationToken);

        var record = new StoredBlob(
            id,
            key,
            BlobState.Ready,
            // Intermediate: a run made it and nobody has asked for it. It becomes durable only if
            // it reaches a root execution's output stream, which nothing knows yet.
            BlobRetention.Intermediate,
            BlobOrigin.Produced,
            mediaType,
            stat.Size,
            now,
            name);

        await repository.AddAsync(record, cancellationToken);
        await repository.AttachToRunTreeAsync(id, provenance.RootExecution, cancellationToken);
        await repository.RecordEventAsync(id, BlobEventKind.Produced, now, provenance, cancellationToken);

        // The size the store reports, not what anyone predicted: a response body has no trustworthy
        // length in advance.
        return WorkflowBlob.Stored(id.Value, stat.Size, mediaType, name: name);
    }

    public async Task<byte[]> ReadAsync(
        WorkflowBlob blob, ExecutionId runTree, CancellationToken cancellationToken)
    {
        // An inline blob carries its own bytes. No lookup, no access check: there is nothing to
        // resolve and nothing an id could be forged against.
        if (blob.Data is { } inline)
            return inline;

        if (blob.Id is null)
            throw new BlobUnreadableException("This blob has neither bytes nor an id.");

        var id = new BlobId(blob.Id);
        var record = await repository.FindAsync(id, cancellationToken);

        // Every refusal below is the same exception with the same code, deliberately. "No such
        // blob", "deleted", and "not yours" must be indistinguishable, or the difference tells a
        // caller which ids exist.
        if (record is null || !record.IsReadable)
            throw new BlobUnreadableException($"'{blob.Id}' cannot be read.");

        if (!await repository.BelongsToRunTreeAsync(id, runTree, cancellationToken))
            throw new BlobUnreadableException($"'{blob.Id}' cannot be read.");

        if (store is null)
            throw new BlobUnreadableException($"'{blob.Id}' cannot be read: no blob store is configured.");

        try
        {
            await using var stream = await store.OpenReadAsync(
                record.Key, blob.Offset, blob.Size, cancellationToken);

            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();

            return bytes;
        }
        catch (ObjectStoreException failure)
        {
            throw new BlobUnreadableException($"'{blob.Id}' cannot be read.", failure);
        }
    }

    public async Task<int> PromoteAsync(
        IReadOnlyCollection<WorkflowBlob> blobs, ExecutionId runTree, CancellationToken cancellationToken)
    {
        // Inline blobs have no record to promote: their bytes are the value, so they are kept or lost
        // with whatever carries them and no retention rule applies.
        var ids = blobs
            .Where(blob => blob.Id is not null && blob.Data is null)
            .Select(blob => new BlobId(blob.Id!))
            .Distinct()
            .ToArray();

        if (ids.Length == 0) return 0;

        var records = await repository.FindManyAsync(ids, cancellationToken);
        var promoted = 0;

        foreach (var id in ids)
        {
            // An id that resolves to nothing, or to something already durable, is not an error. A
            // result may name the same blob twice, and a run may return a handle a client attached,
            // which was durable before it arrived.
            if (!records.TryGetValue(id, out var record) || record.Retention == BlobRetention.Durable)
                continue;

            // The access rule again, and for a reason beyond tidiness: a value can only carry out
            // what its tree could read, so a handle naming anything else was forged — and promoting
            // it would let a document pin somebody else's object in storage for good.
            if (!await repository.BelongsToRunTreeAsync(id, runTree, cancellationToken))
                continue;

            await repository.UpdateAsync(record.Promoted(), cancellationToken);
            promoted++;
        }

        return promoted;
    }
}

/// <summary>How the blobs module is configured (<c>api/docs/binary-data.md</c> § Limits).</summary>
public sealed class BlobOptions
{
    public const string SectionName = "Blobs";

    /// <summary>
    /// The largest blob carried inline. Above it the bytes go to the store and the value becomes a
    /// handle.
    /// <para>
    /// A few hundred kibibytes, as the spec advises, and the reasoning is durability rather than
    /// memory: every inline byte is copied at every hop and written to history, and in this daemon a
    /// response reaches the interpreter as a Dapr activity input — so an inline body is serialized
    /// into the orchestration history on every transition of the run. A megabyte-scale threshold
    /// would quietly turn one large response into tens of megabytes of durable state.
    /// </para>
    /// <para>
    /// Invisible to authors either way: no document can tell which backing a blob has, which is what
    /// makes this a deployment's choice rather than part of the contract.
    /// </para>
    /// </summary>
    public long InlineThresholdBytes { get; set; } = 256 * 1024;

    /// <summary>How often the sweep runs. Zero or less turns it off, which is a supported deployment.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many blobs one query of the sweep considers, and so how big one transaction is.</summary>
    public int SweepBatchSize { get; set; } = 200;

    /// <summary>
    /// How many batches one pass will do before leaving the rest to the next interval. A first sweep
    /// after a long outage has an unbounded amount of work, and a pass that tried to finish it would
    /// hold a connection for hours.
    /// </summary>
    public int SweepMaxBatches { get; set; } = 25;

    /// <summary>§ When intermediates go. See <see cref="BlobRetentionPolicy"/> for what each means.</summary>
    public TimeSpan FailedRunWindow { get; set; } = BlobRetentionPolicy.Default.FailedRunWindow;

    public TimeSpan AbandonedUploadWindow { get; set; } = BlobRetentionPolicy.Default.AbandonedUploadWindow;

    public TimeSpan InterruptedRunWindow { get; set; } = BlobRetentionPolicy.Default.InterruptedRunWindow;

    public TimeSpan SweepGracePeriod { get; set; } = BlobRetentionPolicy.Default.GracePeriod;

    public BlobRetentionPolicy RetentionPolicy() =>
        new(FailedRunWindow, AbandonedUploadWindow, InterruptedRunWindow, SweepGracePeriod);
}
