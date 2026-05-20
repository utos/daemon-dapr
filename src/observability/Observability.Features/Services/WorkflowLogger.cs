using Common.SeedWork;
using Observability.Domain;
using Observability.Domain.Events;

namespace Observability.Features.Services;

public class WorkflowLogger(ILogStreamRepository logStreamRepository) : IWorkflowLogger
{
    private static readonly Dictionary<WorkflowLogLevel, Utos.Daemon.V1.LogLevel> LevelMap = new()
    {
        [WorkflowLogLevel.Debug] = Utos.Daemon.V1.LogLevel.Debug,
        [WorkflowLogLevel.Info] = Utos.Daemon.V1.LogLevel.Info,
        [WorkflowLogLevel.Warn] = Utos.Daemon.V1.LogLevel.Warn,
        [WorkflowLogLevel.Error] = Utos.Daemon.V1.LogLevel.Error
    };

    public async Task LogAsync(Guid executionId, string source, string? category,
        WorkflowLogLevel level, string message)
    {
        var protoLevel = LevelMap[level];
        var logEvent = new WorkflowLogEvent(executionId, source, category, protoLevel, message, DateTime.UtcNow);
        await logStreamRepository.AppendAsync(executionId, logEvent);
    }

    public async Task TerminateAsync(Guid executionId)
    {
        var terminatedEvent = new StreamTerminatedEvent(DateTime.UtcNow);
        await logStreamRepository.AppendAsync(executionId, terminatedEvent);
    }
}
