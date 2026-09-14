using System.Runtime.CompilerServices;
using Common.SeedWork;
using Mediator;
using Microsoft.Extensions.Logging;
using Observability.Application.Exceptions;
using Observability.Application.Models;
using Observability.Application.Ports;
using Observability.Domain;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Observability.Application.Queries;

/// <summary>
/// Follows an execution's log stream until it terminates. <paramref name="After"/> and
/// <paramref name="Tail"/> are mutually exclusive on the wire, so no check is needed here.
/// </summary>
public record WatchExecutionQuery(
    ExecutionId ExecutionId,
    int? After,
    int? Tail,
    ExecutionFrameLevel? MinLevel,
    string? Source,
    string? Category) : IStreamQuery<ExecutionFrame>;

public class WatchExecutionHandler(
    IExecutionProbe probe,
    IExecutionLogStore logStore,
    ILogger<WatchExecutionHandler> logger)
    : IStreamQueryHandler<WatchExecutionQuery, ExecutionFrame>
{
    public async IAsyncEnumerable<ExecutionFrame> Handle(
        WatchExecutionQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation("Starting stream for execution {ExecutionId}", query.ExecutionId);

        if (!await probe.ExistsAsync(query.ExecutionId, ct))
            throw new ExecutionNotFoundException(query.ExecutionId);

        var stream = logStore.SubscribeAsync(query.ExecutionId, query.After, query.Tail, ct);

        await foreach (var item in stream.WithCancellation(ct))
        {
            var frame = Project(item, query.ExecutionId);

            if (frame is null || !Matches(frame, query))
                continue;

            yield return frame;
        }
    }

    private static ExecutionFrame? Project(ExecutionStreamItem item, ExecutionId executionId) => item switch
    {
        LogItem log => new ExecutionFrame(
            log.Entry.Timestamp,
            MapLevel(log.Entry.Level),
            log.Entry.Source,
            log.Entry.Category,
            WithoutExecutionSuffix(log.Entry.Message, executionId)),

        TerminalItem terminal => new ExecutionFrame(
            terminal.Timestamp,
            terminal.Outcome == ExecutionOutcome.Failed
                ? ExecutionFrameLevel.Error
                : ExecutionFrameLevel.Info,
            FrameSource.System,
            string.Empty,
            terminal.Outcome == ExecutionOutcome.Failed
                ? "Execution failed"
                : "Execution completed",
            terminal.Outcome,
            terminal.FailureReason),

        _ => null
    };

    /// <summary>
    /// Drops the <c>(execution {id})</c> suffix the daemon's log calls carry, when it names the
    /// execution this stream belongs to.
    /// <para>
    /// The suffix earns its place in the host's own logs, where runs interleave and the id is the
    /// only way to tell them apart. A watch stream is already scoped to one run, so there it repeats
    /// the one thing the reader asked for on every line. Only an exact suffix naming this stream's
    /// execution is removed: a message naming some other execution keeps it, because there it is
    /// information. Which workflow in the tree wrote a line is the frame's source, not this text.
    /// </para>
    /// </summary>
    public static string WithoutExecutionSuffix(string message, ExecutionId executionId)
    {
        var suffix = $" (execution {executionId})";
        return message.EndsWith(suffix, StringComparison.Ordinal) ? message[..^suffix.Length] : message;
    }

    private static bool Matches(ExecutionFrame frame, WatchExecutionQuery query)
    {
        if (query.MinLevel is { } minimum && frame.Level < minimum)
            return false;
        if (query.Source is not null && frame.Source != query.Source)
            return false;
        if (query.Category is not null && frame.Category != query.Category)
            return false;
        return true;
    }

    private static ExecutionFrameLevel MapLevel(MsLogLevel level) => level switch
    {
        MsLogLevel.Trace or MsLogLevel.Debug => ExecutionFrameLevel.Debug,
        MsLogLevel.Information => ExecutionFrameLevel.Info,
        MsLogLevel.Warning => ExecutionFrameLevel.Warn,
        MsLogLevel.Error or MsLogLevel.Critical => ExecutionFrameLevel.Error,
        _ => ExecutionFrameLevel.Unspecified
    };
}
