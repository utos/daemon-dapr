using Common.Infrastructure.Logging;
using Execution.Infrastructure.Dapr;
using Microsoft.Extensions.Logging;

namespace UnitTests.Dapr;

/// <summary>
/// A sub-workflow logs into its root's watch stream. Without attribution a child's
/// <c>Activity 'refund' completed</c> reads exactly like one the root wrote — so the property has
/// to arrive, and the message must arrive untouched beside it.
/// </summary>
public class WorkflowSourceLoggerTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Adds_the_workflow_that_wrote_the_entry_as_a_property()
    {
        var inner = new CapturingLogger();

        WorkflowSourceLogger.For(inner, "settle-refund:2.1.0")
            .LogInformation("Activity '{WorkflowActivity}' started (execution {WorkflowExecutionId})", "refund", Id);

        var entry = Assert.Single(inner.Entries);
        Assert.Contains(new KeyValuePair<string, object?>(LogProperty.WorkflowSource, "settle-refund:2.1.0"), entry.Properties);
    }

    [Fact]
    public void Keeps_the_message_and_every_original_property()
    {
        // WorkflowExecutionId is what routes the entry to a stream at all. Losing it to the
        // decoration would drop the line rather than attribute it.
        var inner = new CapturingLogger();

        WorkflowSourceLogger.For(inner, "settle-refund:2.1.0")
            .LogInformation("Activity '{WorkflowActivity}' started (execution {WorkflowExecutionId})", "refund", Id);

        var entry = Assert.Single(inner.Entries);
        Assert.Equal($"Activity 'refund' started (execution {Id})", entry.Message);
        Assert.Contains(new KeyValuePair<string, object?>("WorkflowActivity", "refund"), entry.Properties);
        Assert.Contains(new KeyValuePair<string, object?>(LogProperty.WorkflowExecutionId, Id), entry.Properties);
    }

    [Fact]
    public void Leaves_the_root_workflows_logger_undecorated()
    {
        var inner = new CapturingLogger();

        Assert.Same(inner, WorkflowSourceLogger.For(inner, null));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception),
                (state as IReadOnlyList<KeyValuePair<string, object?>> ?? []).ToList()));
    }
}
