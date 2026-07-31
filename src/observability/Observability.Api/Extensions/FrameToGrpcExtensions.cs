using Google.Protobuf.WellKnownTypes;
using Observability.Application.Models;
using Observability.Domain;
using Utos.Daemon.V1;
using DaemonLogLevel = Utos.Daemon.V1.LogLevel;
using GrpcWorkflowError = Utos.Workflow.V1.WorkflowError;

namespace Observability.Api.Extensions;

public static class FrameToGrpcExtensions
{
    /// <summary>
    /// Coarse failure code carried on the streamed <see cref="GrpcWorkflowError"/>. Mirrors the
    /// query-side constant in <c>DtoToGrpcExtensions.ExecutionFailedCode</c> until the execution
    /// pipeline records structured failure causes of its own.
    /// </summary>
    private const string ExecutionFailedCode = "EXECUTION_FAILED";

    public static WatchExecutionResponse ToGrpc(this ExecutionFrame frame)
    {
        var response = new WatchExecutionResponse
        {
            Source = frame.Source,
            Category = frame.Category,
            Level = frame.Level.ToGrpc(),
            Message = frame.Message,
            Timestamp = Timestamp.FromDateTime(frame.Timestamp.ToUniversalTime())
        };

        if (frame.Terminal is not { } outcome)
            return response;

        var failed = outcome == ExecutionOutcome.Failed;
        response.Status = failed ? ExecutionStatus.Failed : ExecutionStatus.Completed;

        if (failed)
            response.Error = new GrpcWorkflowError
            {
                Code = ExecutionFailedCode,
                Message = frame.FailureReason ?? string.Empty
            };

        return response;
    }

    // Both directions are 1:1 by design: ExecutionFrameLevel exists to mirror this enum without
    // dragging the wire format into the application layer.

    public static DaemonLogLevel ToGrpc(this ExecutionFrameLevel level) => level switch
    {
        ExecutionFrameLevel.Debug => DaemonLogLevel.Debug,
        ExecutionFrameLevel.Info => DaemonLogLevel.Info,
        ExecutionFrameLevel.Warn => DaemonLogLevel.Warn,
        ExecutionFrameLevel.Error => DaemonLogLevel.Error,
        _ => DaemonLogLevel.Unspecified
    };

    public static ExecutionFrameLevel ToDomain(this DaemonLogLevel level) => level switch
    {
        DaemonLogLevel.Debug => ExecutionFrameLevel.Debug,
        DaemonLogLevel.Info => ExecutionFrameLevel.Info,
        DaemonLogLevel.Warn => ExecutionFrameLevel.Warn,
        DaemonLogLevel.Error => ExecutionFrameLevel.Error,
        _ => ExecutionFrameLevel.Unspecified
    };
}
