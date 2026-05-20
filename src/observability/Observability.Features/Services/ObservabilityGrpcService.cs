using Common.SeedWork;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Observability.Domain;
using Observability.Features.Mapping;
using Utos.Daemon.V1;

namespace Observability.Features.Services;

public class ObservabilityGrpcService(
    IEventStoreRepository eventStore,
    ILogStreamRepository logStreamRepository,
    ILogger<ObservabilityGrpcService> logger)
    : ObservabilityService.ObservabilityServiceBase
{
    public override async Task WatchExecution(WatchExecutionRequest request,
        IServerStreamWriter<WatchExecutionResponse> responseStream, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        // Validate mutually exclusive parameters
        if (request is { HasTail: true, HasAfter: true })
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "Parameters 'tail' and 'after' are mutually exclusive"));

        logger.LogInformation("Starting stream for execution {ExecutionId}", executionId);

        try
        {
            if (!await eventStore.ExistsAsync(StreamNames.Execution(executionId),
                    context.CancellationToken))
                throw new RpcException(new Status(StatusCode.NotFound, "Execution not found"));

            int? afterPosition = request.HasAfter ? request.After : null;
            var events = logStreamRepository.SubscribeAsync(executionId, afterPosition,
                context.CancellationToken);

            await foreach (var eventWithNumber in events)
            {
                logger.LogInformation("Processing event {EventNumber} of type {EventType} for execution {ExecutionId}",
                    eventWithNumber.EventNumber,
                    eventWithNumber.Event.GetType().Name,
                    executionId);

                // Close the stream when StreamTerminatedEvent is received
                if (eventWithNumber.Event is StreamTerminatedEvent)
                {
                    logger.LogInformation("Stream terminated for execution {ExecutionId}, closing stream",
                        executionId);
                    break;
                }

                var watchResponse = WatchEventMapper.ConvertToWatchResponse(eventWithNumber.Event, executionId);
                if (watchResponse != null && WatchEventMapper.ShouldIncludeEvent(watchResponse, request))
                    await responseStream.WriteAsync(watchResponse);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Stream cancelled for execution {ExecutionId}", executionId);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            logger.LogError(ex, "Error processing events for execution {ExecutionId}", executionId);
            throw new RpcException(new Status(StatusCode.Internal, "Internal error"));
        }
    }

    public override Task<GetHealthResponse> GetHealth(GetHealthRequest request, ServerCallContext context)
    {
        var version = typeof(ObservabilityGrpcService).Assembly.GetName().Version?.ToString() ?? "unknown";

        return Task.FromResult(new GetHealthResponse
        {
            Status = HealthStatus.Healthy,
            Version = version
        });
    }
}
