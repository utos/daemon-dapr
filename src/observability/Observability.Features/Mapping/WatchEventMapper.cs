using Common.SeedWork;
using Observability.Domain.Events;
using Utos.Daemon.V1;

namespace Observability.Features.Mapping;

public static class WatchEventMapper
{
    public static WatchExecutionResponse? ConvertToWatchResponse(IDomainEvent domainEvent, Guid executionId)
    {
        return domainEvent switch
        {
            WorkflowLogEvent e => new WatchExecutionResponse
            {
                Source = e.Source,
                Category = e.Category,
                Level = e.Level,
                Message = e.Message,
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(e.DateOccurred.ToUniversalTime())
            },
            _ => null
        };
    }

    public static bool ShouldIncludeEvent(WatchExecutionResponse watchResponse, WatchExecutionRequest request)
    {
        if (request.HasLevel && watchResponse.Level < request.Level)
            return false;

        if (request.HasSource && watchResponse.Source != request.Source)
            return false;

        if (request.HasCategory && watchResponse.Category != request.Category)
            return false;

        return true;
    }
}
