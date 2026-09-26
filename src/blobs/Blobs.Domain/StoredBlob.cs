using Common.Blobs;
using Common.SeedWork;

namespace Blobs.Domain;

/// <summary>
/// An opaque identifier the daemon minted for a stored blob.
/// <para>
/// <b>Not an address.</b> It is not a storage key, a URL, or a content digest. A daemon resolves it
/// by lookup in its own records, and only within the run trees the blob belongs to — which is what
/// makes forging one useless: it gets an attacker a failure, not bytes. That is also why the spec
/// needs no signature on a handle.
/// </para>
/// </summary>
public readonly record struct BlobId(string Value)
{
    /// <summary>
    /// A fresh id. Time-ordered at the front so that listing and paging by id is listing by
    /// creation, and random at the back so it is not guessable — neither property is required by
    /// the spec, and both are cheap.
    /// </summary>
    public static BlobId New(DateTimeOffset at) =>
        new("b_" + at.ToUnixTimeMilliseconds().ToString("x11") + Guid.NewGuid().ToString("n")[..12]);

    public override string ToString() => Value;
}

/// <summary>Where a blob is in its life. <c>utos.daemon.v1.BlobState</c>.</summary>
public enum BlobState
{
    /// <summary>Created, with an upload location issued, and not yet completed.</summary>
    Pending,

    /// <summary>The bytes are there. The only state in which a blob can be read.</summary>
    Ready,

    /// <summary>
    /// Removed. The record outlives the bytes on purpose: <c>GetBlob</c> answers <c>DELETED</c> for
    /// an id it issued, so reading an old run's result looks like cleanup rather than corruption.
    /// </summary>
    Deleted,
}

/// <summary>
/// Whether anybody asked for this blob. <c>utos.daemon.v1.BlobRetention</c>.
/// <para>
/// The distinction the spec keeps is not where a blob came from — an upload and a returned result
/// are both things a person will look for later — but whether somebody asked for it. A run that
/// fetches five things and returns one made four that nobody asked for, and those are what fill a
/// bucket without anyone noticing.
/// </para>
/// </summary>
public enum BlobRetention
{
    /// <summary>A run produced it and nothing carried it out. The daemon removes it.</summary>
    Intermediate,

    /// <summary>
    /// A client uploaded it, or it reached a root execution's output stream. Removed only on
    /// request — <c>DeleteBlob</c>, <c>PruneBlobs</c>, or <c>DeleteExecution</c> with
    /// <c>delete_blobs</c>.
    /// </summary>
    Durable,
}

/// <summary>Who made it. Metadata, deliberately not a retention class.</summary>
public enum BlobOrigin
{
    Uploaded,
    Produced,
}

/// <summary>
/// What the daemon knows about a stored blob: enough to resolve a handle to bytes, to decide
/// whether it may be read, and to say when it can go.
/// <para>
/// Deliberately not the value. <c>Common.Workflow.WorkflowBlob</c> is what travels through a run
/// and what a client sees; this is the daemon's record behind an id, and the two are separate
/// because a handle is copied at every hop while this is written once and updated rarely.
/// </para>
/// <para>
/// Run-tree membership is <em>not</em> a field here. A durable upload can be attached to any number
/// of runs, so the set is unbounded and loading it to answer one access check would be the wrong
/// shape; <see cref="IBlobRepository.BelongsToRunTreeAsync"/> asks the question instead.
/// </para>
/// </summary>
/// <param name="Size">
/// What the store holds, not what a client declared. A client's declared size decides whether the
/// upload is single or multipart; the real length is read back at <c>CompleteBlob</c>.
/// </param>
/// <param name="Name">Present exactly when the blob is a <c>File</c> rather than a plain <c>Blob</c>.</param>
/// <param name="Producer">
/// What made it, when a run did — <c>BlobInfo.producer</c>. Held on the blob rather than read back out
/// of its history, because <c>GetBlob</c> answers it and scanning a timeline to fill one field of a
/// lookup is the wrong shape. Plain data, so deleting the execution does not erase why the blob exists.
/// </param>
/// <param name="CompletedAt">
/// When the bytes arrived: a client's <c>CompleteBlob</c>, or the write for a blob a run produced —
/// which is <see cref="CreatedAt"/> for one of those, since nothing exists until the bytes do.
/// </param>
/// <param name="DeletedAt">
/// When the bytes went. Set with <see cref="BlobState.Deleted"/> and not otherwise, so a record that
/// says <c>DELETED</c> can always say when.
/// </param>
public sealed record StoredBlob(
    BlobId Id,
    ObjectKey Key,
    BlobState State,
    BlobRetention Retention,
    BlobOrigin Origin,
    string MediaType,
    long Size,
    DateTimeOffset CreatedAt,
    string? Name = null,
    DateTimeOffset? LastModified = null,
    BlobProducer? Producer = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? DeletedAt = null)
{
    /// <summary>A blob whose bytes a run may read, subject to the run tree it belongs to.</summary>
    public bool IsReadable => State == BlobState.Ready;

    /// <summary>
    /// The upload finished: the bytes are there, and <paramref name="size"/> is what the store
    /// actually holds rather than what the client said it would.
    /// <para>
    /// A client's upload is durable from the moment it completes — somebody asked for it — so
    /// there is no separate promotion step for one.
    /// </para>
    /// </summary>
    public StoredBlob Completed(long size, DateTimeOffset at, string? mediaType = null) =>
        this with
        {
            State = BlobState.Ready,
            Size = size,
            MediaType = string.IsNullOrEmpty(mediaType) ? MediaType : mediaType,
            CompletedAt = at,
        };

    /// <summary>
    /// Intermediate to durable, because the blob reached a root execution's output stream — a run
    /// returned it or emitted it, so somebody asked for it after all.
    /// <para>
    /// Irreversible, and it never copies bytes. A blob's class cannot be known when it is written,
    /// since nothing knows yet whether it will be returned; a design that kept the two classes in
    /// separate places would make this a server-side copy of an object that may be gigabytes.
    /// </para>
    /// </summary>
    public StoredBlob Promoted() =>
        Retention == BlobRetention.Durable ? this : this with { Retention = BlobRetention.Durable };

    /// <summary>
    /// The bytes are gone. The record stays, so an id the daemon issued keeps answering for as long
    /// as any execution record mentions it.
    /// </summary>
    public StoredBlob Removed(DateTimeOffset at) =>
        this with { State = BlobState.Deleted, DeletedAt = at };
}

/// <summary>
/// What made a blob, when an execution did — <c>utos.daemon.v1.BlobProducer</c>.
/// <para>
/// The same five facts <c>BlobUsage</c> carries, and deliberately a separate type: that one is a
/// cross-module <em>argument</em>, describing what a caller was doing, while this is a field of a
/// record that will outlive the run. A durable blob's producer has to still read correctly after the
/// execution is deleted, which is why nothing here is a reference to anything.
/// </para>
/// </summary>
public sealed record BlobProducer(
    ExecutionId Execution,
    ExecutionId RootExecution,
    string Workflow,
    string? WorkflowDigest = null,
    string? Activity = null)
{
    public static BlobProducer From(BlobUsage usage) =>
        new(usage.Execution, usage.RootExecution, usage.Workflow, usage.WorkflowDigest, usage.Activity);
}
