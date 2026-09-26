using Blobs.Domain;
using Mediator;

namespace Blobs.Application.Queries;

/// <summary>Blobs, newest first, filtered. <c>ListBlobs</c>.</summary>
public sealed record ListBlobsQuery(BlobQuery Filter, int PageSize, string PageToken)
    : IQuery<BlobPage>;

public sealed class ListBlobsHandler(IBlobRepository repository)
    : IQueryHandler<ListBlobsQuery, BlobPage>
{
    public async ValueTask<BlobPage> Handle(ListBlobsQuery query, CancellationToken ct) =>
        await repository.ListAsync(query.Filter, query.PageSize, query.PageToken, ct);
}

/// <summary>A blob's timeline, with the blob. <c>GetBlobHistory</c>.</summary>
public sealed record GetBlobHistoryQuery(BlobId Id) : IQuery<BlobTimeline?>;

public sealed record BlobTimeline(BlobSummary Summary, IReadOnlyList<BlobHistoryEntry> Events);

public sealed class GetBlobHistoryHandler(IBlobRepository repository)
    : IQueryHandler<GetBlobHistoryQuery, BlobTimeline?>
{
    public async ValueTask<BlobTimeline?> Handle(GetBlobHistoryQuery query, CancellationToken ct)
    {
        var summary = await repository.SummaryAsync(query.Id, ct);

        if (summary is null) return null;

        return new BlobTimeline(summary, await repository.HistoryAsync(query.Id, ct));
    }
}
