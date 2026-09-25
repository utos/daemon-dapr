using Blobs.Application.Commands;
using Blobs.Application.Queries;
using Blobs.Domain;
using Common.SeedWork;
using Google.Protobuf.WellKnownTypes;
using Utos.Daemon.V1;
using Domain = Blobs.Domain;
using Wire = Utos.Daemon.V1;

namespace Blobs.Api.Extensions;

/// <summary>
/// Proto ↔ domain for the blob service, and the only place either side knows about the other.
/// <para>
/// Written out rather than conventional, for the reason <c>BlobEntity</c>'s mapping is: the enums exist
/// on both sides with different spellings and different zero values — the wire has an
/// <c>_UNSPECIFIED</c> member and the domain deliberately does not — so anything that mapped them by
/// ordinal or by name would be one added member away from being silently wrong.
/// </para>
/// </summary>
internal static class BlobMapping
{
    internal static BlobInfo ToProto(this BlobSummary summary)
    {
        var blob = summary.Blob;

        var info = new BlobInfo
        {
            Id = blob.Id.Value,
            State = blob.State.ToProto(),
            Retention = blob.Retention.ToProto(),
            Origin = blob.Origin.ToProto(),
            Size = blob.Size,
            MediaType = blob.MediaType,
            CreatedAt = blob.CreatedAt.ToTimestamp(),
            UsageCount = summary.UsageCount,
        };

        if (blob.Name is { } name) info.Name = name;
        if (blob.LastModified is { } modified) info.LastModified = modified.ToTimestamp();
        if (blob.CompletedAt is { } completed) info.CompletedAt = completed.ToTimestamp();
        if (blob.DeletedAt is { } deleted) info.DeletedAt = deleted.ToTimestamp();
        if (summary.LastUsedAt is { } used) info.LastUsedAt = used.ToTimestamp();
        if (blob.Producer is { } producer) info.Producer = producer.ToProto();

        // `digest` is deliberately absent. The proto says "present only when the daemon hashed them
        // itself while writing them", and this daemon streams a response body straight to the store
        // without teeing it through a hash — so there is nothing honest to put here. Never the store's
        // entity tag, which is not a content hash for a multipart object.

        return info;
    }

    private static Wire.BlobProducer ToProto(this Domain.BlobProducer producer)
    {
        var proto = new Wire.BlobProducer
        {
            ExecutionId = producer.Execution.ToString(),
            RootExecutionId = producer.RootExecution.ToString(),
            Activity = producer.Activity ?? string.Empty,
        };

        // The workflow as it was resolved, name and version together with the digest of the exact
        // bundle that ran — a name and version stop identifying what ran once a bundle is rebuilt.
        if (WorkflowReferenceOf(producer.Workflow, producer.WorkflowDigest) is { } workflow)
            proto.Workflow = workflow;

        return proto;
    }

    /// <summary>
    /// A workflow reference, parsed back out of the canonical <c>name:version</c> string the records
    /// keep.
    /// <para>
    /// Kept as one string rather than as fields, because what a record has to survive is the execution
    /// being deleted, and a canonical identity does that without three columns. Split here rather than
    /// handed over whole: <c>WorkflowReference.name</c> is a name, and putting <c>thing:1.0.0</c> in it
    /// would make a client's display and any round trip through it wrong.
    /// </para>
    /// <para>
    /// A string that does not parse is answered as a name alone rather than dropped — it is still the
    /// most the daemon knows. Split on the <em>last</em> colon, because a registry-qualified name
    /// contains others.
    /// </para>
    /// </summary>
    private static WorkflowReference? WorkflowReferenceOf(string? workflow, string? digest)
    {
        if (string.IsNullOrEmpty(workflow)) return null;

        var separator = workflow.LastIndexOf(':');

        var reference = separator > 0
            ? new WorkflowReference
            {
                Name = workflow[..separator],
                Version = workflow[(separator + 1)..],
            }
            : new WorkflowReference { Name = workflow };

        if (digest is { } pinned) reference.Digest = pinned;

        return reference;
    }

    internal static Wire.BlobState ToProto(this Domain.BlobState state) => state switch
    {
        Domain.BlobState.Pending => Wire.BlobState.Pending,
        Domain.BlobState.Ready => Wire.BlobState.Ready,
        Domain.BlobState.Deleted => Wire.BlobState.Deleted,
        _ => Wire.BlobState.Unspecified,
    };

    internal static Wire.BlobRetention ToProto(this Domain.BlobRetention retention) => retention switch
    {
        Domain.BlobRetention.Durable => Wire.BlobRetention.Durable,
        Domain.BlobRetention.Intermediate => Wire.BlobRetention.Intermediate,
        _ => Wire.BlobRetention.Unspecified,
    };

    internal static Wire.BlobOrigin ToProto(this Domain.BlobOrigin origin) => origin switch
    {
        Domain.BlobOrigin.Uploaded => Wire.BlobOrigin.Uploaded,
        Domain.BlobOrigin.Produced => Wire.BlobOrigin.Produced,
        _ => Wire.BlobOrigin.Unspecified,
    };

    internal static Wire.BlobEventKind ToProto(this Domain.BlobEventKind kind) => kind switch
    {
        Domain.BlobEventKind.Uploaded => Wire.BlobEventKind.Uploaded,
        Domain.BlobEventKind.Produced => Wire.BlobEventKind.Produced,
        Domain.BlobEventKind.Attached => Wire.BlobEventKind.Attached,
        Domain.BlobEventKind.Read => Wire.BlobEventKind.Read,
        Domain.BlobEventKind.Downloaded => Wire.BlobEventKind.Downloaded,
        _ => Wire.BlobEventKind.Unspecified,
    };

    internal static BlobEvent ToProto(this BlobHistoryEntry entry)
    {
        var proto = new BlobEvent
        {
            Kind = entry.Kind.ToProto(),
            FirstAt = entry.FirstAt.ToTimestamp(),
            LastAt = entry.LastAt.ToTimestamp(),
            Executions = entry.Executions,
        };

        if (entry.RootExecution is { } root) proto.RootExecutionId = root.ToString();
        if (entry.Activity is { } activity) proto.Activity = activity;

        if (WorkflowReferenceOf(entry.Workflow, entry.WorkflowDigest) is { } workflow)
            proto.Workflow = workflow;

        return proto;
    }

    internal static Utos.Workflows.V1.Blob ToProto(this WorkflowBlobHandle handle)
    {
        var blob = new Utos.Workflows.V1.Blob
        {
            Id = handle.Id,
            Size = handle.Size,
            MediaType = handle.MediaType,
        };

        if (handle.Name is { } name) blob.Name = name;
        if (handle.LastModified is { } modified) blob.LastModified = modified.ToTimestamp();

        return blob;
    }

    internal static Wire.SignedLocation ToProto(this Common.Blobs.SignedLocation location)
    {
        var proto = new Wire.SignedLocation
        {
            Url = location.Url,
            Method = location.Method,
            ExpiresAt = location.ExpiresAt.ToTimestamp(),
        };

        foreach (var (name, value) in location.Headers)
            proto.Headers.Add(name, value);

        return proto;
    }

    /// <summary>
    /// A listing's filter. Every field is optional on the wire and every one of them is optional here,
    /// so an unset field is <c>null</c> rather than a zero value that would read as
    /// <c>_UNSPECIFIED</c> and filter on nothing.
    /// </summary>
    internal static BlobQuery ToDomain(this ListBlobsRequest request) =>
        new(request.HasRetention ? request.Retention.ToDomain() : null,
            request.HasOrigin ? request.Origin.ToDomain() : null,
            request.HasState ? request.State.ToDomain() : null,
            request.HasRootExecutionId && ExecutionId.TryParse(request.RootExecutionId, out var root)
                ? root
                : null,
            request.CreatedBefore?.ToDateTimeOffset(),
            request.LastUsedBefore?.ToDateTimeOffset());

    /// <summary>
    /// A prune's filter — the same type, with the two fields <c>PruneBlobsRequest</c> does not have left
    /// unset. <c>state</c> is one of them, and the handler decides it; <c>root_execution_id</c> is the
    /// other, because deleting a run tree's blobs is <c>DeleteExecution</c>'s job.
    /// </summary>
    internal static BlobQuery ToDomain(this PruneBlobsRequest request) =>
        new(request.HasRetention ? request.Retention.ToDomain() : null,
            request.HasOrigin ? request.Origin.ToDomain() : null,
            State: null,
            RootExecution: null,
            request.CreatedBefore?.ToDateTimeOffset(),
            request.LastUsedBefore?.ToDateTimeOffset());

    /// <summary>
    /// Unspecified maps to <c>null</c>, which is "do not filter on this". A caller that sends the zero
    /// value has asked for nothing, not for a member that does not exist on this side.
    /// </summary>
    private static Domain.BlobRetention? ToDomain(this Wire.BlobRetention retention) => retention switch
    {
        Wire.BlobRetention.Durable => Domain.BlobRetention.Durable,
        Wire.BlobRetention.Intermediate => Domain.BlobRetention.Intermediate,
        _ => null,
    };

    private static Domain.BlobOrigin? ToDomain(this Wire.BlobOrigin origin) => origin switch
    {
        Wire.BlobOrigin.Uploaded => Domain.BlobOrigin.Uploaded,
        Wire.BlobOrigin.Produced => Domain.BlobOrigin.Produced,
        _ => null,
    };

    private static Domain.BlobState? ToDomain(this Wire.BlobState state) => state switch
    {
        Wire.BlobState.Pending => Domain.BlobState.Pending,
        Wire.BlobState.Ready => Domain.BlobState.Ready,
        Wire.BlobState.Deleted => Domain.BlobState.Deleted,
        _ => null,
    };

    internal static PruneBlobsResponse ToProto(this PruneResult result) =>
        new() { Count = result.Count, Bytes = result.Bytes, Skipped = result.Skipped };
}
