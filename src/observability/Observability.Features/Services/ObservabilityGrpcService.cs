using System.Reflection;
using Common.SeedWork;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Observability.Domain;
using Utos.Daemon.V1;
using DaemonLogLevel = Utos.Daemon.V1.LogLevel;
using GrpcWorkflowError = Utos.Workflow.V1.WorkflowError;

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
        if (!ExecutionId.TryParse(request.ExecutionId, out var executionId))
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

            await foreach (var item in logStore.SubscribeAsync(executionId, after, tail, context.CancellationToken))
            {
                // Terminal frames carry the same level/source/category as a log line and go
                // through the same filter: a COMPLETED frame (Info, source "system") is excluded
                // by an activity/error filter, while a FAILED frame (Error) still passes a
                // level>=Error filter. An unfiltered watcher sees every terminal frame.
                var response = item switch
                {
                    LogItem log => BuildLogResponse(log.Entry),
                    TerminalItem terminal => BuildTerminalResponse(terminal),
                    _ => null
                };

                if (response is null || !ShouldInclude(response, request))
                    continue;

                await responseStream.WriteAsync(response);
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

    /// <summary>
    /// Coarse failure code carried on the streamed <see cref="GrpcWorkflowError"/>. Mirrors the
    /// query-side constant in <c>DtoToGrpcExtensions.ExecutionFailedCode</c> until the execution
    /// pipeline records structured failure causes of its own.
    /// </summary>
    private const string ExecutionFailedCode = "EXECUTION_FAILED";

    private static WatchExecutionResponse BuildLogResponse(ExecutionLogEntry entry) => new()
    {
        Source = entry.Source,
        Category = entry.Category,
        Level = MapLogLevel(entry.Level),
        Message = entry.Message,
        Timestamp = Timestamp.FromDateTime(entry.Timestamp.ToUniversalTime())
    };

    private static WatchExecutionResponse BuildTerminalResponse(TerminalItem terminal)
    {
        var failed = terminal.Outcome == ExecutionOutcome.Failed;
        var response = new WatchExecutionResponse
        {
            Source = "system",
            Level = failed ? DaemonLogLevel.Error : DaemonLogLevel.Info,
            Message = failed ? "Execution failed" : "Execution completed",
            Timestamp = Timestamp.FromDateTime(terminal.Timestamp.ToUniversalTime()),
            Status = failed ? ExecutionStatus.Failed : ExecutionStatus.Completed
        };

        if (failed)
            response.Error = new GrpcWorkflowError
            {
                Code = ExecutionFailedCode,
                Message = terminal.FailureReason ?? string.Empty
            };

        return response;
    }

    private static bool ShouldInclude(WatchExecutionResponse response, WatchExecutionRequest request)
    {
        if (request.HasLevel && response.Level < request.Level)
            return false;
        if (request.HasSource && response.Source != request.Source)
            return false;
        if (request.HasCategory && response.Category != request.Category)
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
