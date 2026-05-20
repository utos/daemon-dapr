using IntegrationTests.Shared.Fixtures;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for execution state transition events.
///
/// NOTE: These tests are skipped because execution state events (Created, Scheduled, Active,
/// Completed, Failed) are no longer emitted on the WatchExecution observability stream.
/// The observability stream now only carries log events from a separate executionlog-{id} stream.
/// State transitions are still tracked in the aggregate stream (workflowexecution-{id}) but
/// are not exposed through the WatchExecution API.
///
/// Lifecycle events are instead verified via system log messages in SystemLogTests.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class ExecutionStateEventsTests(DaemonTestFixture fixture)
{
    private const string SkipReason =
        "Execution state events are no longer emitted on the WatchExecution observability stream";

    [Fact(Skip = SkipReason)]
    public Task SuccessfulWorkflow_ShouldTransitionThroughAllStates()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task ShouldEmitInitialStateEvent()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task ShouldEmitScheduledStateEvent()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task ShouldEmitActiveStateEvent()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task ShouldEmitCompletedStateEvent()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task FailedWorkflow_ShouldTransitionToFailedState()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task ShouldEmitFailedStateEventOnError()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task StateEventsShouldIncludeTimestamps()
    {
        return Task.CompletedTask;
    }

    [Fact(Skip = SkipReason)]
    public Task StateEventsShouldMaintainExecutionId()
    {
        return Task.CompletedTask;
    }
}
