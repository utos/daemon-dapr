using System.Reflection;
using Common.SeedWork;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Observability.Domain;
using Utos.Daemon.V1;
using DaemonLogLevel = Utos.Daemon.V1.LogLevel;

namespace Observability.Features.Services;

public class ObservabilityGrpcService(
    IEventStoreRepository eventStore,
    IExecutionLogStore logStore,
    ILogger<ObservabilityGrpcService> logger)
    : ObservabilityService.ObservabilityServiceBase
{
    public override async Task WatchExecution(WatchExecutionRequest request,
        IServerStreamWriter<WatchExecutionResponse> responseStream, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        // 'tail' and 'after' are a oneof since v0.0.10 — the wire format now makes them
        // mutually exclusive, so no explicit check is needed.
        logger.LogInformation("Starting stream for execution {ExecutionId}", executionId);

        try
        {
            if (!await eventStore.ExistsAsync(StreamNames.Execution(executionId), context.CancellationToken))
                throw new RpcException(new Status(StatusCode.NotFound, "Execution not found"));

            int? after = request.HasAfter ? request.After : null;
            int? tail = request.HasTail ? request.Tail : null;

            await foreach (var entry in logStore.SubscribeAsync(executionId, after, tail, context.CancellationToken))
            {
                if (!ShouldIncludeEntry(entry, request))
                    continue;

                await responseStream.WriteAsync(new WatchExecutionResponse
                {
                    Source = entry.Source,
                    Category = entry.Category,
                    Level = MapLogLevel(entry.Level),
                    Message = entry.Message,
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                        entry.Timestamp.ToUniversalTime())
                });
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Stream cancelled for execution {ExecutionId}", executionId);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            logger.LogError(ex, "Error streaming logs for execution {ExecutionId}", executionId);
            throw new RpcException(new Status(StatusCode.Internal, "Internal error"));
        }
    }

    public override Task<GetHealthResponse> GetHealth(GetHealthRequest request, ServerCallContext context)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        return Task.FromResult(new GetHealthResponse
        {
            Status = HealthStatus.Healthy,
            Version = version
        });
    }

    private static bool ShouldIncludeEntry(ExecutionLogEntry entry, WatchExecutionRequest request)
    {
        if (request.HasLevel && MapLogLevel(entry.Level) < request.Level)
            return false;
        if (request.HasSource && entry.Source != request.Source)
            return false;
        if (request.HasCategory && entry.Category != request.Category)
            return false;
        return true;
    }

    private static DaemonLogLevel MapLogLevel(Microsoft.Extensions.Logging.LogLevel level) => level switch
    {
        Microsoft.Extensions.Logging.LogLevel.Trace
            or Microsoft.Extensions.Logging.LogLevel.Debug => DaemonLogLevel.Debug,
        Microsoft.Extensions.Logging.LogLevel.Information => DaemonLogLevel.Info,
        Microsoft.Extensions.Logging.LogLevel.Warning => DaemonLogLevel.Warn,
        Microsoft.Extensions.Logging.LogLevel.Error
            or Microsoft.Extensions.Logging.LogLevel.Critical => DaemonLogLevel.Error,
        _ => DaemonLogLevel.Unspecified
    };
}
