using Common.Workflow;
using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

/// <summary>
/// What running out of rules means on a path that is consuming a sub-workflow's output.
///
/// It used to mean the same thing everywhere: the run is over. On a handler that made the explicit
/// back-edge to the call activity load-bearing — omit it and the subscription ends, the producer is
/// cancelled, and the run reports Completed. A workflow meant to poll for weeks would exit after
/// one value with nothing to show for it.
///
/// Now a handler that finishes goes back for the next value, the way a promise branch returns to
/// its promise. The failure arm is asserted alongside deliberately: it is the one case that must
/// *not* resume, and the two rules meet in the same method.
/// </summary>
public class HandlerResumeTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    /// <summary>The consumer: a call activity plus the handler chain it dispatches to.</summary>
    private static WorkflowGraph Consumer(
        TransitionRule[] onEmitted,
        WorkflowActivity? handler = null)
    {
        return Wf.Graph(
            ("watch", Wf.ConsumingCall("acme/producer:1.0.0", "poll", onEmitted)),
            ("process", handler ?? Wf.Http("https://api.example.com/ingest")),
            ("store", Wf.Http("https://api.example.com/store")));
    }

    [Fact]
    public void A_handler_rule_list_that_matches_nothing_takes_the_next_value()
    {
        // A filtering consumer: values it does not care about must be skipped, not fatal. This is
        // the trap the old behaviour set — one uninteresting value silently killed the producer.
        var graph = Consumer([Wf.Goto("process", condition: "{{ output.urgent }}")]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["urgent"] = false },
            ActivityOutcomeKind.Emitted);

        var go = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("watch", go.ActivityName);
    }

    [Fact]
    public void An_empty_handler_rule_list_takes_the_next_value()
    {
        var decision = _interpreter.Advance(
            Wf.Session(Consumer([])), "watch", null,
            new Dictionary<string, object>(), ActivityOutcomeKind.Emitted);

        var go = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("watch", go.ActivityName);
    }

    [Fact]
    public void A_handler_that_runs_out_of_rules_returns_to_the_call_activity()
    {
        // The whole point: `process` declares no onSuccess at all, and that now means "this
        // handler is done" rather than "the run is over".
        var decision = _interpreter.Advance(
            Wf.Session(Consumer([Wf.Goto("process")])), "process", null,
            new Dictionary<string, object>(),
            dispatchedBy: "watch");

        var go = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("watch", go.ActivityName);
    }

    [Fact]
    public void The_end_of_a_multi_activity_handler_chain_returns_to_the_call_activity()
    {
        // The coupling this removes is non-local: in process -> store, the back-edge used to live
        // in `store`, which then knew it belonged to one particular call activity and could not be
        // reused anywhere else.
        var graph = Consumer(
            [Wf.Goto("process")],
            handler: Wf.Http("https://api.example.com/ingest", onSuccess: Wf.Goto("store")));

        var decision = _interpreter.Advance(
            Wf.Session(graph), "store", null, new Dictionary<string, object>(),
            dispatchedBy: "watch");

        var go = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("watch", go.ActivityName);
    }

    [Fact]
    public void A_failure_inside_a_handler_propagates_rather_than_taking_the_next_value()
    {
        // Where the two rules collide, and the one case that must not resume. Going back for the
        // next value here would swallow the failure and keep the run alive as though nothing had
        // happened — the same class of bug as reporting a failed run as Completed.
        var graph = Consumer([Wf.Goto("process")]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "process", null, null,
            ActivityOutcomeKind.Failed,
            error: new WorkflowErrorContext("EXECUTION_FAILED", "boom"),
            dispatchedBy: "watch");

        Assert.IsType<TransitionDecision.Propagate>(decision);
    }

    [Fact]
    public void A_handler_that_transitions_somewhere_explicit_still_goes_there()
    {
        // Resuming is what an *exhausted* rule list means. A rule that matched is untouched, which
        // is what keeps `- transition: { name: end }` working as the way to stop consuming.
        var graph = Consumer(
            [Wf.Goto("process")],
            handler: Wf.Http("https://api.example.com/ingest", onSuccess: Wf.Goto("end")));

        var decision = _interpreter.Advance(
            Wf.Session(graph), "process", null, new Dictionary<string, object>(),
            dispatchedBy: "watch");

        var go = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("end", go.ActivityName);
    }

    [Fact]
    public void An_ordinary_path_with_no_handler_above_it_still_ends_the_run()
    {
        // The default is unchanged where nothing dispatched the path. Only a run that is actually
        // inside a handler resumes.
        var decision = _interpreter.Advance(
            Wf.Session(Consumer([Wf.Goto("process")])), "process", null,
            new Dictionary<string, object>());

        Assert.IsType<TransitionDecision.End>(decision);
    }
}
