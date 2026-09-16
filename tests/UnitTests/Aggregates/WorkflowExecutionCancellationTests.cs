using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Aggregates;
using Execution.Domain.Events;

namespace UnitTests.Aggregates;

/// <summary>
/// Cancellation follows the same two rules as the other terminals — guards in the command method,
/// never in <c>When</c>, and first terminal state wins — but it is the only one a *client* asks
/// for directly, so the losing cases are the ones worth pinning.
/// </summary>
public class WorkflowExecutionCancellationTests
{
    private static readonly ExecutionId Id = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static WorkflowExecution Created() =>
        new(new WorkflowBundle("test:1.0.0", new Dictionary<string, Workflow>()),
            new Dictionary<string, object>(),
            "start",
            executionId: Id);

    private static IReadOnlyList<IDomainEvent> Emitted(WorkflowExecution execution) =>
        execution.GetDomainEvents().ToList();

    [Fact]
    public void An_active_run_can_be_cancelled()
    {
        var execution = Created();
        execution.Schedule();
        execution.MarkActive();
        execution.ClearDomainEvents();

        execution.Cancel("user asked");

        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.True(execution.IsTerminal);
        Assert.Equal("user asked", execution.CancellationReason);
        Assert.IsType<WorkflowExecutionCancelledEvent>(Assert.Single(Emitted(execution)));
    }

    [Fact]
    public void A_run_can_be_cancelled_before_it_ever_went_active()
    {
        // Cancelling a queued run is the ordinary case for "stop it, I changed my mind".
        var execution = Created();
        execution.Schedule();
        execution.ClearDomainEvents();

        execution.Cancel();

        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.Null(execution.CancellationReason);
    }

    [Fact]
    public void Cancelling_twice_emits_one_event()
    {
        var execution = Created();
        execution.MarkActive();
        execution.Cancel("first");
        execution.ClearDomainEvents();

        execution.Cancel("second");

        Assert.Empty(Emitted(execution));
        Assert.Equal("first", execution.CancellationReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_run_that_already_finished_is_not_rewritten_as_cancelled(bool completed)
    {
        // The whole reason the handler reports FAILED_PRECONDITION: a cancel that loses a race
        // must not turn a run that succeeded — or failed for a real reason — into "cancelled".
        var execution = Created();
        execution.MarkActive();

        if (completed) execution.Complete();
        else execution.Fail("boom");

        execution.ClearDomainEvents();

        execution.Cancel("too late");

        Assert.Empty(Emitted(execution));
        Assert.Equal(completed ? ExecutionStatus.Completed : ExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.CancellationReason);
    }

    [Fact]
    public void A_cancelled_run_cannot_be_completed_or_failed_afterwards()
    {
        // The orchestration is torn down asynchronously, so its own terminal transition can still
        // arrive after the cancel landed. It must not win.
        var execution = Created();
        execution.MarkActive();
        execution.Cancel("stop");
        execution.ClearDomainEvents();

        execution.Complete();
        execution.Fail("late failure");

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.Null(execution.FailureReason);
    }

    [Fact]
    public void A_cancellation_reason_never_becomes_a_failure_reason()
    {
        var execution = Created();
        execution.MarkActive();

        execution.Cancel("budget exhausted");

        Assert.Equal("budget exhausted", execution.CancellationReason);
        Assert.Null(execution.FailureReason);
    }

    [Fact]
    public void A_replayed_cancellation_restores_status_and_reason()
    {
        var execution = new WorkflowExecution();
        IDomainEvent[] history =
        [
            new WorkflowExecutionCreatedEvent(
                Id,
                new WorkflowBundle("test:1.0.0", new Dictionary<string, Workflow>()),
                new Dictionary<string, object>(),
                "start",
                DateTime.UtcNow),
            new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow),
            new WorkflowExecutionCancelledEvent(Id, "user asked", DateTime.UtcNow)
        ];

        execution.LoadFromHistory(history, history.Length - 1);

        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.Equal("user asked", execution.CancellationReason);
        Assert.True(execution.IsTerminal);
    }
}
