using Common.ExecutionLifecycle;
using Common.SeedWork;
using Execution.Domain.Events;
using Execution.Infrastructure.EventHandling;

namespace UnitTests.EventHandling;

/// <summary>
/// The seam that lets observability stop referencing execution. It is the only place the three
/// terminal domain events are read on another context's behalf, so what it forwards — and that a
/// listener's failure still reaches the caller — is the whole contract.
/// </summary>
public class ExecutionLifecycleNotifierTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTime At = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_completed_event_reaches_every_listener()
    {
        var first = new RecordingListener();
        var second = new RecordingListener();
        var notifier = new ExecutionLifecycleNotifier([first, second]);

        await notifier.HandleAsync(new WorkflowExecutionCompletedEvent(Id, At), position: 0);

        Assert.Equal([(Id, At)], first.Completed);
        Assert.Equal([(Id, At)], second.Completed);
        Assert.Empty(first.Failed);
    }

    [Fact]
    public async Task A_failed_event_carries_its_reason_through()
    {
        var listener = new RecordingListener();
        var notifier = new ExecutionLifecycleNotifier([listener]);

        await notifier.HandleAsync(new WorkflowExecutionFailedEvent(Id, "HTTP 500", At), position: 0);

        Assert.Equal([(Id, At, "HTTP 500")], listener.Failed);
        Assert.Empty(listener.Completed);
    }

    [Fact]
    public async Task A_cancelled_event_is_a_third_ending_and_not_a_failure()
    {
        // It used to reach nobody. The notifier read the two events its own module calls terminal,
        // and a cancelled run was announced to no listener at all — so the blobs module would never
        // learn its tree had finished and would keep the run's intermediates until the sweep's age
        // backstop found them weeks later.
        var listener = new RecordingListener();
        var notifier = new ExecutionLifecycleNotifier([listener]);

        await notifier.HandleAsync(new WorkflowExecutionCancelledEvent(Id, "user asked", At), position: 0);

        Assert.Equal([(Id, At, "user asked")], listener.Cancelled);
        Assert.Empty(listener.Failed);
        Assert.Empty(listener.Completed);
    }

    [Fact]
    public async Task A_listener_that_does_not_care_about_cancellation_need_not_say_so()
    {
        // The one method with a default body, and this is what it buys: what a *cancelled* run's
        // terminal watch frame should say is an open question with a wire shape attached, and
        // retention is not the change that should answer it. So observability keeps the behaviour it
        // had rather than being made to invent one.
        var notifier = new ExecutionLifecycleNotifier([new IndifferentListener()]);

        await notifier.HandleAsync(new WorkflowExecutionCancelledEvent(Id, null, At), position: 0);
    }

    [Fact]
    public async Task A_throwing_listener_propagates()
    {
        // The event processor parks an event whose handler throws. Swallowing here would ack a
        // side effect that never happened, and the terminal frame would be lost silently.
        var notifier = new ExecutionLifecycleNotifier([new ThrowingListener()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.HandleAsync(new WorkflowExecutionCompletedEvent(Id, At), position: 0));
    }

    [Theory]
    [InlineData(typeof(WorkflowExecutionCompletedEvent), true)]
    [InlineData(typeof(WorkflowExecutionFailedEvent), true)]
    [InlineData(typeof(WorkflowExecutionCancelledEvent), true)]
    [InlineData(typeof(WorkflowExecutionCreatedEvent), false)]
    [InlineData(typeof(WorkflowExecutionScheduledEvent), false)]
    [InlineData(typeof(WorkflowExecutionActiveEvent), false)]
    public void Only_the_terminal_events_are_announced(Type eventType, bool expected)
    {
        var notifier = new ExecutionLifecycleNotifier([]);

        Assert.Equal(expected, notifier.CanHandle(eventType));
    }

    private sealed class RecordingListener : IExecutionLifecycleListener
    {
        public List<(ExecutionId, DateTime)> Completed { get; } = [];
        public List<(ExecutionId, DateTime, string)> Failed { get; } = [];
        public List<(ExecutionId, DateTime, string?)> Cancelled { get; } = [];

        public Task OnCompletedAsync(ExecutionId executionId, DateTime occurredAt, CancellationToken ct = default)
        {
            Completed.Add((executionId, occurredAt));
            return Task.CompletedTask;
        }

        public Task OnFailedAsync(
            ExecutionId executionId, DateTime occurredAt, string reason, string? code = null,
            CancellationToken ct = default)
        {
            Failed.Add((executionId, occurredAt, reason));
            return Task.CompletedTask;
        }

        public Task OnCancelledAsync(
            ExecutionId executionId, DateTime occurredAt, string? reason, CancellationToken ct = default)
        {
            Cancelled.Add((executionId, occurredAt, reason));
            return Task.CompletedTask;
        }
    }

    /// <summary>A listener that implements only what it must — the default body carries the rest.</summary>
    private sealed class IndifferentListener : IExecutionLifecycleListener
    {
        public Task OnCompletedAsync(ExecutionId executionId, DateTime occurredAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task OnFailedAsync(
            ExecutionId executionId, DateTime occurredAt, string reason, string? code = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingListener : IExecutionLifecycleListener
    {
        public Task OnCompletedAsync(ExecutionId executionId, DateTime occurredAt, CancellationToken ct = default) =>
            throw new InvalidOperationException("listener failed");

        public Task OnFailedAsync(
            ExecutionId executionId, DateTime occurredAt, string reason, string? code = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("listener failed");
    }
}
