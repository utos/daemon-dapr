using Common.SeedWork;
using Execution.Domain.Aggregates;
using Execution.Domain.Events;
using Common.Workflow;

namespace UnitTests.Aggregates;

public class WorkflowExecutionTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static WorkflowExecution Created()
    {
        return new WorkflowExecution(
            new WorkflowBundle("test:1.0.0", new Dictionary<string, Workflow>()),
            new Dictionary<string, object>(),
            "start",
            executionId: Id);
    }

    /// <summary>An execution rehydrated from the given history, as the store would load it.</summary>
    private static WorkflowExecution Replayed(params IDomainEvent[] history)
    {
        var execution = new WorkflowExecution();
        execution.LoadFromHistory(history, history.Length - 1);
        return execution;
    }

    private static WorkflowExecutionCreatedEvent CreatedEvent()
    {
        return new WorkflowExecutionCreatedEvent(
            Id,
            new WorkflowBundle("test:1.0.0", new Dictionary<string, Workflow>()),
            new Dictionary<string, object>(),
            "start",
            DateTime.UtcNow);
    }

    private static IReadOnlyList<IDomainEvent> Emitted(WorkflowExecution execution)
    {
        return execution.GetDomainEvents().ToList();
    }

    // ---- progression ---------------------------------------------------

    [Fact]
    public void A_run_progresses_created_scheduled_active_completed()
    {
        var execution = Created();
        Assert.Equal(ExecutionStatus.Unspecified, execution.Status);

        execution.Schedule();
        Assert.Equal(ExecutionStatus.Scheduled, execution.Status);

        execution.MarkActive();
        Assert.Equal(ExecutionStatus.Active, execution.Status);

        execution.Complete();
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
        Assert.True(execution.IsTerminal);

        Assert.Equal(
            [
                typeof(WorkflowExecutionCreatedEvent),
                typeof(WorkflowExecutionScheduledEvent),
                typeof(WorkflowExecutionActiveEvent),
                typeof(WorkflowExecutionCompletedEvent)
            ],
            Emitted(execution).Select(e => e.GetType()));
    }

    [Fact]
    public void A_run_may_go_active_without_ever_being_marked_scheduled()
    {
        // The Scheduled event can be parked by the event processor, which must not leave the
        // run stuck short of Active.
        var execution = Created();

        execution.MarkActive();

        Assert.Equal(ExecutionStatus.Active, execution.Status);
    }

    // ---- idempotency ---------------------------------------------------

    [Fact]
    public void Marking_active_twice_emits_one_event()
    {
        // The orchestration driving this is at-least-once: a worker that dies after the write
        // but before its result is recorded re-runs the transition.
        var execution = Replayed(CreatedEvent(), new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow));

        execution.MarkActive();

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Active, execution.Status);
    }

    [Fact]
    public void Scheduling_a_run_that_is_already_active_emits_nothing()
    {
        var execution = Replayed(CreatedEvent(), new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow));

        execution.Schedule();

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Active, execution.Status);
    }

    [Fact]
    public void Marking_active_after_the_run_finished_emits_nothing()
    {
        var execution = Replayed(CreatedEvent(), new WorkflowExecutionCompletedEvent(Id, DateTime.UtcNow));

        execution.MarkActive();

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
    }

    // ---- first terminal wins -------------------------------------------

    [Fact]
    public void Failing_after_completing_is_ignored()
    {
        // Reachable: the Completed write lands but its acknowledgement is lost, the retry
        // throws into the executor's catch block, and that block reports a failure.
        var execution = Created();
        execution.Complete();
        execution.ClearDomainEvents();

        execution.Fail("worker crashed");

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
        Assert.Null(execution.FailureReason);
    }

    [Fact]
    public void Completing_after_failing_is_ignored_and_keeps_the_original_reason()
    {
        var execution = Created();
        execution.Fail("connection refused");
        execution.ClearDomainEvents();

        execution.Complete();

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("connection refused", execution.FailureReason);
    }

    [Fact]
    public void Failing_twice_keeps_the_first_reason()
    {
        var execution = Created();
        execution.Fail("the real cause");
        execution.ClearDomainEvents();

        execution.Fail("a later, less useful cause");

        Assert.Empty(Emitted(execution));
        Assert.Equal("the real cause", execution.FailureReason);
    }

    // ---- failure reason ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_failure_without_a_reason_still_describes_itself(string? reason)
    {
        // The reason is surfaced as a WorkflowError message, where a null would read as a
        // daemon bug rather than a workflow failure.
        var execution = Created();

        execution.Fail(reason);

        Assert.False(string.IsNullOrWhiteSpace(execution.FailureReason));
        var failed = Assert.IsType<WorkflowExecutionFailedEvent>(Emitted(execution).Last());
        Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
    }

    [Fact]
    public void A_failure_reason_is_carried_onto_the_event()
    {
        var execution = Created();

        execution.Fail("HTTP 500 from https://example.com");

        var failed = Assert.IsType<WorkflowExecutionFailedEvent>(Emitted(execution).Last());
        Assert.Equal("HTTP 500 from https://example.com", failed.Reason);
        Assert.Equal("HTTP 500 from https://example.com", execution.FailureReason);
    }

    // ---- replay --------------------------------------------------------

    [Fact]
    public void A_stream_with_a_duplicate_scheduled_event_still_replays()
    {
        // Guards belong in the command methods, not in the fold. A guard that throws during
        // replay makes a stream that already contains a contradiction impossible to load, and
        // the history is whatever was written before the rules existed.
        var execution = Replayed(
            CreatedEvent(),
            new WorkflowExecutionScheduledEvent(Id, DateTime.UtcNow),
            new WorkflowExecutionScheduledEvent(Id, DateTime.UtcNow));

        Assert.Equal(Id, execution.Id);
        Assert.Equal(ExecutionStatus.Scheduled, execution.Status);
    }

    [Fact]
    public void A_legacy_stream_holding_both_terminal_events_replays_last_write_wins()
    {
        // Matching how the read model projects status, so the two do not disagree about a
        // stream written before the guards existed.
        var execution = Replayed(
            CreatedEvent(),
            new WorkflowExecutionCompletedEvent(Id, DateTime.UtcNow),
            new WorkflowExecutionFailedEvent(Id, "later failure", DateTime.UtcNow));

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal("later failure", execution.FailureReason);
        Assert.True(execution.IsTerminal);
    }

    [Fact]
    public void Replay_restores_terminal_state_so_a_late_transition_is_refused()
    {
        var execution = Replayed(
            CreatedEvent(),
            new WorkflowExecutionScheduledEvent(Id, DateTime.UtcNow),
            new WorkflowExecutionActiveEvent(Id, DateTime.UtcNow),
            new WorkflowExecutionFailedEvent(Id, "boom", DateTime.UtcNow));

        execution.Complete();

        Assert.Empty(Emitted(execution));
        Assert.Equal(ExecutionStatus.Failed, execution.Status);
    }

    // ---- version -------------------------------------------------------

    [Fact]
    public void A_fresh_aggregate_expects_no_stream()
    {
        Assert.Equal(AggregateRoot.NoStream, new WorkflowExecution().Version);

        // Uncommitted events do not advance the version — nothing is on the stream yet.
        Assert.Equal(AggregateRoot.NoStream, Created().Version);
    }

    [Fact]
    public void Replay_takes_the_revision_it_is_given_rather_than_counting_events()
    {
        // An event the store cannot deserialize is dropped from the sequence but still
        // occupies a revision, so counting would drift.
        var execution = new WorkflowExecution();

        execution.LoadFromHistory([CreatedEvent()], 4);

        Assert.Equal(4, execution.Version);
    }

    [Fact]
    public void Committing_advances_the_version_by_the_number_appended()
    {
        var execution = Created();
        execution.Schedule();

        execution.MarkCommitted(2);

        // Two events appended to an empty stream leaves it at revision 1.
        Assert.Equal(1, execution.Version);
    }

    [Fact]
    public void Committing_onto_an_existing_stream_advances_from_its_revision()
    {
        var execution = Replayed(CreatedEvent(), new WorkflowExecutionScheduledEvent(Id, DateTime.UtcNow));
        Assert.Equal(1, execution.Version);

        execution.MarkActive();
        execution.MarkCommitted(1);

        Assert.Equal(2, execution.Version);
    }
}
