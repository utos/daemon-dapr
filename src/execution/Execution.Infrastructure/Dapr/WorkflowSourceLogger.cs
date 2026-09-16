using System.Collections;
using Common.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.Dapr;

/// <summary>
/// Tags every entry with the workflow that wrote it, as a structured
/// <see cref="LogProperty.WorkflowSource"/> property.
/// <para>
/// A sub-workflow logs into its root's watch stream, so without this a child's
/// <c>Activity 'refund' completed</c> is indistinguishable from one the root wrote. It is a
/// decorator rather than a logging scope for two reasons: a scope would have to survive Dapr's
/// replay-safe logger, and it cannot cross into an activity, which runs in its own invocation —
/// where the HTTP call that is most worth attributing is logged. The message text is untouched;
/// the property rides alongside it for any provider that reads properties.
/// </para>
/// </summary>
public class WorkflowSourceLogger(ILogger inner, string source) : ILogger
{
    /// <summary>The logger itself when there is no source to add — the root workflow's case.</summary>
    public static ILogger For(ILogger logger, string? source) =>
        string.IsNullOrEmpty(source) ? logger : new WorkflowSourceLogger(logger, source);

    /// <inheritdoc cref="For(ILogger, string?)"/>
    public static ILogger<T> For<T>(ILogger<T> logger, string? source) =>
        string.IsNullOrEmpty(source) ? logger : new WorkflowSourceLogger<T>(logger, source);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!inner.IsEnabled(logLevel)) return;

        var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        inner.Log(logLevel, eventId, new SourcedState(properties, source, formatter(state, exception)),
            exception, static (s, _) => s.Message);
    }

    /// <summary>The original properties plus the source, rendering exactly the original message.</summary>
    private sealed class SourcedState(IReadOnlyList<KeyValuePair<string, object?>> properties, string source, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message => message;

        public int Count => properties.Count + 1;

        public KeyValuePair<string, object?> this[int index] =>
            index < properties.Count ? properties[index] : new(LogProperty.WorkflowSource, source);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var property in properties) yield return property;
            yield return new(LogProperty.WorkflowSource, source);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => message;
    }
}

/// <summary>The typed form, so a decorated logger still satisfies an <c>ILogger&lt;T&gt;</c> parameter.</summary>
public sealed class WorkflowSourceLogger<T>(ILogger<T> inner, string source)
    : WorkflowSourceLogger(inner, source), ILogger<T>;
