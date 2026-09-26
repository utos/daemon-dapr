namespace Blobs.Infrastructure.Data;

/// <summary>
/// A stored blob's record. Three tables rather than one document, because the two things that grow
/// — which run trees a blob belongs to, and what has happened to it — are queried on their own and
/// must not be loaded to answer a question about the blob itself.
/// </summary>
/// <remarks>
/// The enums are persisted as <b>strings</b>, not ordinals. A column holding <c>1</c> means whatever
/// the enum's second member happens to be today, so adding a member in the wrong place silently
/// reinterprets every existing row; <c>"Ready"</c> cannot be reinterpreted. Rows here outlive the
/// runs that made them by design — a durable blob's whole point — so this is a contract, not a
/// preference.
/// </remarks>
public sealed class BlobEntity
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Where the bytes are in the store. Never shown to a client: an id is not an address.</summary>
    public string ObjectKey { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Retention { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Set exactly when the blob is a <c>File</c> rather than a plain <c>Blob</c>.</summary>
    public string? Name { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the bytes arrived — a client's <c>CompleteBlob</c>, or a run's write.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Set with <see cref="State"/> <c>Deleted</c> and not otherwise.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    // What produced it, flattened rather than owned. Five nullable columns beat a second table for
    // something read on every lookup and written once, and beat an owned entity for something whose
    // absence is meaningful: all five are null exactly when `Origin` is `Uploaded`.
    public Guid? ProducerExecutionId { get; set; }

    public Guid? ProducerRootExecutionId { get; set; }

    public string? ProducerWorkflow { get; set; }

    public string? ProducerWorkflowDigest { get; set; }

    public string? ProducerActivity { get; set; }
}

/// <summary>
/// One run tree a blob belongs to — the whole of the spec's access rule, as a row.
/// <para>
/// A table rather than a collection on <see cref="BlobEntity"/>: a durable upload may be attached to
/// any number of runs, and the only question ever asked of this is "is this pair present?", which is
/// one indexed lookup rather than a load.
/// </para>
/// </summary>
public sealed class BlobRunTreeEntity
{
    public string BlobId { get; set; } = string.Empty;

    /// <summary>
    /// The root of the run tree. Stored as data, not as a foreign key to an execution: a durable
    /// blob outlives its run, and deleting the execution must not cascade into the blob's record.
    /// </summary>
    public Guid RootExecutionId { get; set; }

    public DateTimeOffset AttachedAt { get; set; }

    /// <summary>
    /// When the run tree reached a terminal state, or null while it has not. The sweep's whole
    /// question: a blob with any null here belongs to a tree that could still read it, and the spec's
    /// one hard requirement is that it stays.
    /// </summary>
    public DateTimeOffset? TreeEndedAt { get; set; }

    /// <summary>
    /// How it ended, as a string for the reason every other enum here is one. Null exactly when
    /// <see cref="TreeEndedAt"/> is.
    /// </summary>
    public string? TreeOutcome { get; set; }
}

/// <summary>
/// One row of a blob's timeline. Unique on (blob, kind, root execution, workflow), which is what
/// makes recording an event an upsert and keeps the row count bounded by how many distinct
/// documents touched the blob rather than by fan-out.
/// </summary>
public sealed class BlobHistoryEntity
{
    public long Id { get; set; }

    public string BlobId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    /// <summary>Null for <c>uploaded</c> and <c>downloaded</c>, which involve no execution.</summary>
    public Guid? RootExecutionId { get; set; }

    public string? Workflow { get; set; }

    /// <summary>
    /// The bundle's content digest beside its name. A name and version stop identifying what ran
    /// once a bundle is rebuilt; the digest does not.
    /// </summary>
    public string? WorkflowDigest { get; set; }

    public string? Activity { get; set; }

    public DateTimeOffset FirstAt { get; set; }

    public DateTimeOffset LastAt { get; set; }

    /// <summary>How many distinct executions this row stands for, after fan-out collapsed into it.</summary>
    public int Executions { get; set; }
}
