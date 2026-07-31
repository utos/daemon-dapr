using System.Reflection;
using Common.SeedWork;
using Grpc.Core;
using Mediator;
using Microsoft.Extensions.Logging;
using Observability.Api.Extensions;
using Observability.Application.Exceptions;
using Observability.Application.Queries;
using Utos.Daemon.V1;

namespace Observability.Api.Services;

public class ObservabilityGrpcService(
    IMediator mediator,
    ILogger<ObservabilityGrpcService> logger)
    : ObservabilityService.ObservabilityServiceBase
{
    public override async Task WatchExecution(WatchExecutionRequest request,
        IServerStreamWriter<WatchExecutionResponse> responseStream, ServerCallContext context)
    {
        if (!ExecutionId.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        // 'tail' and 'after' are a oneof since v0.0.10 — the wire format now makes them
        // mutually exclusive, so no explicit check is needed.
        var query = new WatchExecutionQuery(
            executionId,
            request.HasAfter ? request.After : null,
            request.HasTail ? request.Tail : null,
            request.HasLevel ? request.Level.ToDomain() : null,
            request.HasSource ? request.Source : null,
            request.HasCategory ? request.Category : null);

        try
        {
            await foreach (var frame in mediator.CreateStream(query, context.CancellationToken))
                await responseStream.WriteAsync(frame.ToGrpc());
        }
        catch (ExecutionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
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
}
