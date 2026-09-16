using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

public class WorkflowNavigatorTests
{
    private static ExecutionCursor Fresh()
    {
        return ExecutionCursor.Start(null, false);
    }

    [Theory]
    [InlineData("end")]
    [InlineData("error")]
    [InlineData("End")]
    public void A_former_keyword_is_an_ordinary_unknown_activity(string name)
    {
        // Until spec 0.0.15 these were terminal keywords, matched case-insensitively. They are
        // nothing now: ending a path is a `result` action and failing it an `error` action, and
        // the shared validator refuses a bundle that still transitions to them (UTOS-T003). A
        // bundle that reached the navigator regardless gets the same answer as any other name
        // that is not an activity — not a silent completion.
        var graph = Wf.Graph(("a", Wf.Http("https://example.com")));

        var step = WorkflowNavigator.Next(graph, name, Fresh(), Wf.Limits());

        var fail = Assert.IsType<WorkflowStep.Fail>(step);
        Assert.Equal($"Activity '{name}' not found in workflow 'test:1.0.0'", fail.Reason);
    }

    [Fact]
    public void An_activity_may_be_named_end()
    {
        // Nothing is reserved, so `end` is a name like any other and runs.
        var graph = Wf.Graph(("end", Wf.Http("https://example.com")));

        var step = WorkflowNavigator.Next(graph, "end", Fresh(), Wf.Limits());

        Assert.Equal("end", Assert.IsType<WorkflowStep.Run>(step).ActivityName);
    }

    [Fact]
    public void Unknown_activity_fails_naming_the_workflow()
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com")));

        var step = WorkflowNavigator.Next(graph, "nope", Fresh(), Wf.Limits());

        var fail = Assert.IsType<WorkflowStep.Fail>(step);
        Assert.Equal("Activity 'nope' not found in workflow 'test:1.0.0'", fail.Reason);
    }

    [Fact]
    public void First_visit_runs_the_activity_without_detecting_a_loop()
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com")));

        var step = WorkflowNavigator.Next(graph, "a", Fresh(), Wf.Limits());

        var run = Assert.IsType<WorkflowStep.Run>(step);
        Assert.Equal("a", run.ActivityName);
        Assert.False(run.LoopJustDetected);
        Assert.False(run.Cursor.LoopDetected);
        Assert.Equal(1, run.Cursor.TransitionsThisSegment);
        Assert.Contains("a", run.Cursor.Visited);
    }

    [Fact]
    public void Revisiting_an_activity_detects_a_loop_exactly_once()
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com")));
        var limits = Wf.Limits();

        var first = (WorkflowStep.Run)WorkflowNavigator.Next(graph, "a", Fresh(), limits);
        var second = (WorkflowStep.Run)WorkflowNavigator.Next(graph, "a", first.Cursor, limits);
        var third = (WorkflowStep.Run)WorkflowNavigator.Next(graph, "a", second.Cursor, limits);

        Assert.True(second.LoopJustDetected);
        Assert.True(second.Cursor.LoopDetected);

        // Already reported — the driver should not log the discovery again.
        Assert.False(third.LoopJustDetected);
        Assert.True(third.Cursor.LoopDetected);
    }

    [Fact]
    public void A_long_linear_path_never_counts_as_looping()
    {
        var graph = Wf.Graph(
            ("a", Wf.Http("https://example.com")),
            ("b", Wf.Http("https://example.com")),
            ("c", Wf.Http("https://example.com")));

        var limits = Wf.Limits();
        var cursor = Fresh();

        foreach (var name in new[] { "a", "b", "c" })
        {
            var run = Assert.IsType<WorkflowStep.Run>(WorkflowNavigator.Next(graph, name, cursor, limits));
            cursor = run.Cursor;
        }

        Assert.False(cursor.LoopDetected);
        Assert.Equal(3, cursor.TransitionsThisSegment);
    }

    [Fact]
    public void A_segment_continues_as_new_once_it_is_looping_and_has_done_enough_work()
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com")));
        var limits = Wf.Limits(continueAsNewAfter: 2);

        var first = (WorkflowStep.Run)WorkflowNavigator.Next(graph, "a", Fresh(), limits);
        var second = (WorkflowStep.Run)WorkflowNavigator.Next(graph, "a", first.Cursor, limits);

        var step = WorkflowNavigator.Next(graph, "a", second.Cursor, limits);

        var continuation = Assert.IsType<WorkflowStep.ContinueAsNew>(step);
        Assert.Equal(2, continuation.Cursor.TransitionsThisSegment);
        Assert.True(continuation.Cursor.LoopDetected);
        Assert.Contains("a", continuation.Cursor.Visited);
    }

    [Fact]
    public void A_non_looping_segment_never_continues_as_new()
    {
        var graph = Wf.Graph(
            ("a", Wf.Http("https://example.com")),
            ("b", Wf.Http("https://example.com")),
            ("c", Wf.Http("https://example.com")));

        var limits = Wf.Limits(continueAsNewAfter: 1);
        var cursor = Fresh();

        foreach (var name in new[] { "a", "b", "c" })
        {
            var step = WorkflowNavigator.Next(graph, name, cursor, limits);
            cursor = Assert.IsType<WorkflowStep.Run>(step).Cursor;
        }

        Assert.Equal(3, cursor.TransitionsThisSegment);
    }

    [Fact]
    public void A_resumed_segment_carries_its_loop_state_and_resets_the_segment_count()
    {
        var cursor = ExecutionCursor.Start(["a", "b"], true);

        Assert.True(cursor.LoopDetected);
        Assert.Equal(0, cursor.TransitionsThisSegment);
        Assert.Contains("a", cursor.Visited);
        Assert.Contains("b", cursor.Visited);
    }

    [Fact]
    public void A_fresh_segment_is_not_looping_even_with_an_empty_visited_set()
    {
        Assert.False(ExecutionCursor.Start(null, false).LoopDetected);
        Assert.False(ExecutionCursor.Start([], false).LoopDetected);
    }

    [Fact]
    public void A_loop_has_no_ceiling_so_it_runs_until_the_execution_is_cancelled()
    {
        // The spec permits it — "runs until the execution is cancelled" — and a workflow
        // consuming another's output stream depends on it: it re-enters its call activity once
        // per emitted value, so any finite ceiling would be a countdown to killing a long-lived
        // watcher for doing its job.
        //
        // Continuation is what bounds a run, and it still does: the threshold is left at its
        // default, so the segment restarts rather than growing without limit.
        var graph = Wf.Graph(("poll", Wf.Http("https://example.com")));
        var limits = Wf.Limits();

        var cursor = Fresh();
        var laps = 0;

        // Well past the 10,000 that used to fail a run outright.
        while (laps < 20_000)
        {
            var step = WorkflowNavigator.Next(graph, "poll", cursor, limits);

            // Continuing as new resets the segment, which is the point: history stays bounded
            // while the loop itself carries on.
            cursor = step is WorkflowStep.ContinueAsNew continuation
                ? ExecutionCursor.Start(continuation.Cursor.Visited, continuation.Cursor.LoopDetected)
                : Assert.IsType<WorkflowStep.Run>(step).Cursor;

            if (step is WorkflowStep.Run)
                laps++;
        }

        Assert.True(cursor.LoopDetected);
    }
}
