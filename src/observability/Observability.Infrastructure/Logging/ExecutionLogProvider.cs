using Common.SeedWork;
using Microsoft.Extensions.Logging;
using Observability.Domain;

namespace Observability.Infrastructure.Logging;

[ProviderAlias("ExecutionLog")]
public sealed class ExecutionLogProvider(IExecutionLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ExecutionLogger(store, categoryName);
    public void Dispose() { }
}

internal sealed class ExecutionLogger(IExecutionLogStore store, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> kvps) return;

        ExecutionId? executionId = null;
        string? source = null;

        foreach (var kv in kvps)
        {
            switch (kv.Key)
            {
                case "WorkflowExecutionId":
                    // Callers log the id in whichever form they hold it. A shape missing from
                    // this list is not an error anywhere — the entry is simply dropped — so
                    // every form the daemon uses has to be listed.
                    executionId = kv.Value switch
                    {
                        ExecutionId id => id,
                        Guid g => new ExecutionId(g),
                        string s when ExecutionId.TryParse(s, out var id) => id,
                        _ => null
                    };
                    break;
                case "WorkflowSource":
                    source = kv.Value?.ToString();
                    break;
            }

            if (executionId.HasValue && source != null) break;
        }

        if (!executionId.HasValue) return;

        store.AddEntry(executionId.Value, new ExecutionLogEntry(
            DateTime.UtcNow,
            logLevel,
            formatter(state, exception),
            source ?? FrameSource.System,
            category));
    }
}
