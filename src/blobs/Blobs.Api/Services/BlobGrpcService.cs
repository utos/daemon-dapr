using Blobs.Api.Extensions;
using Blobs.Application.Commands;
using Blobs.Application.Queries;
using Blobs.Domain;
using Common.Blobs;
using Grpc.Core;
using Mediator;
using Utos.Daemon.V1;

namespace Blobs.Api.Services;

/// <summary>
/// <c>BlobService</c> — metadata and signed locations for the blobs a daemon stores.
/// <para>
/// <b>No call carries bytes.</b> A client uploads to and downloads from the store itself, at a
/// short-lived location the daemon asks the store to sign; the daemon is in the data path only for I/O a
/// workflow asked for. That is what keeps a gigabyte upload from going through the daemon's memory, and
/// what lets the daemon hold store credentials a client never sees.
/// </para>
/// <para>
/// This transport translates and delegates, as every other one here does. Every question that resolves
/// an id against the records, decides what may be removed, or signs anything is a handler's — which is
/// why this module has an application layer now that it has a service.
/// </para>
/// </summary>
public class BlobGrpcService(IMediator mediator, IObjectStore? store = null)
    : BlobService.BlobServiceBase
{
    public override async Task<GetBlobResponse> GetBlob(
        GetBlobRequest request, ServerCallContext context)
    {
        RequireStore();

        var details = await mediator.Send(
            new GetBlobQuery(Identify(request.Id), request.IncludeDownload), context.CancellationToken);

        if (details is null) throw NotFound(request.Id);

        var response = new GetBlobResponse { Blob = details.Summary.ToProto() };

        if (details.Handle is { } handle) response.Handle = handle.ToProto();
        if (details.Download is { } download) response.Download = download.ToProto();

        // `uploaded_parts` stays empty: it answers a client resuming a multipart upload, and this daemon
        // has no multipart upload to resume yet. Absent is the honest answer, not zero parts received.

        return response;
    }

    public override async Task<ListBlobsResponse> ListBlobs(
        ListBlobsRequest request, ServerCallContext context)
    {
        RequireStore();

        if (request.HasRootExecutionId
            && !Common.SeedWork.ExecutionId.TryParse(request.RootExecutionId, out _))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "Invalid execution ID format"));

        var page = await mediator.Send(
            new ListBlobsQuery(request.ToDomain(), request.PageSize, request.PageToken),
            context.CancellationToken);

        var response = new ListBlobsResponse { NextPageToken = page.NextPageToken };

        response.Blobs.AddRange(page.Blobs.Select(BlobMapping.ToProto));

        return response;
    }

    public override async Task<GetBlobHistoryResponse> GetBlobHistory(
        GetBlobHistoryRequest request, ServerCallContext context)
    {
        RequireStore();

        var timeline = await mediator.Send(
            new GetBlobHistoryQuery(Identify(request.Id)), context.CancellationToken);

        if (timeline is null) throw NotFound(request.Id);

        var response = new GetBlobHistoryResponse { Blob = timeline.Summary.ToProto() };

        response.Events.AddRange(timeline.Events.Select(BlobMapping.ToProto));

        return response;
    }

    public override async Task<DeleteBlobResponse> DeleteBlob(
        DeleteBlobRequest request, ServerCallContext context)
    {
        RequireStore();

        try
        {
            var deleted = await mediator.Send(
                new DeleteBlobCommand(Identify(request.Id)), context.CancellationToken);

            return new DeleteBlobResponse { Blob = deleted.ToProto() };
        }
        catch (BlobNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (BlobHeldByALiveRunException ex)
        {
            // Not an error the caller made, and not permanent: the blob becomes deletable when the run
            // ends. FAILED_PRECONDITION is the code that says "ask again later", which is why the spec
            // chose it over a refusal that reads as final.
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task<PruneBlobsResponse> PruneBlobs(
        PruneBlobsRequest request, ServerCallContext context)
    {
        RequireStore();

        try
        {
            var result = await mediator.Send(
                new PruneBlobsCommand(request.ToDomain(), request.DryRun), context.CancellationToken);

            return result.ToProto();
        }
        catch (UnfilteredPruneException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    /// <summary>
    /// A blob id as the daemon issued it. Not parsed or validated beyond being present: an id is opaque
    /// and is resolved by lookup, so a malformed one is <c>NOT_FOUND</c> like any other id that names
    /// nothing — telling the two apart would say which shapes the daemon mints.
    /// </summary>
    private static BlobId Identify(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A blob id is required"));

        return new BlobId(id);
    }

    private static RpcException NotFound(string id) =>
        new(new Status(StatusCode.NotFound, $"No blob '{id}'."));

    /// <summary>
    /// "A daemon with no store configured answers every call FAILED_PRECONDITION." Every call, including
    /// the read-only ones: without a store there is nothing to list, and answering an empty listing
    /// would let a client conclude its blobs are gone rather than that this daemon never had any.
    /// </summary>
    private void RequireStore()
    {
        if (store is null)
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "This daemon has no blob store configured, so it holds no blobs. "
                + "Configure one with the `blobs` connection string."));
    }
}
