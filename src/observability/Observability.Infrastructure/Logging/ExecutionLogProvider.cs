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
    private const string SystemSource = "system";

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> kvps) return;

        Guid? executionId = null;
        string? source = null;

        foreach (var kv in kvps)
        {
            switch (kv.Key)
            {
                case "WorkflowExecutionId":
                    executionId = kv.Value switch
                    {
                        Guid g => g,
                        string s when Guid.TryParse(s, out var g) => g,
                        _ => null
                    };
                    break;
                case "WorkflowSource":
                    source = kv.Value?.ToString();
                    break;
            }

            if (executionId.HasValue && source != null) break;
        }

        if (executionId == null) return;

        store.AddEntry(executionId.Value, new ExecutionLogEntry(
            DateTime.UtcNow,
            logLevel,
            formatter(state, exception),
            source ?? SystemSource,
            category));
    }
}
