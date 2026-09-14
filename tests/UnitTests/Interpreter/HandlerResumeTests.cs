using Common.Workflow;
using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

/// <summary>
/// What an <c>on_emitted</c> rule list decides, now that its rules dispatch documents rather than
/// transition.
///
/// Most of what this file used to assert is gone with the shape it asserted about. A handler was
/// a chain of activities in the consumer's own graph, so "ran out of rules" had to mean one thing
/// inside a handler and another outside it, and the driver had to carry a stack of which call
/// activity each path was under to tell them apart. A handler is a document now: its path ending
/// ends its own execution, and the consumer learns of it by the child terminating.
///
/// What survives is the arm that had nothing to do with the stack, and it survives because it is
/// the one that was load-bearing: <b>a rule list where nothing matches skips that value.</b> That
/// is what makes a filtering consumer writable, and getting it wrong killed the producer and
/// reported the run Completed after one uninteresting value.
/// </summary>
public class HandlerResumeTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    /// <summary>The consumer: a call activity, plus a document-local activity to dispatch to.</summary>
    private static WorkflowGraph Consumer(EmissionRule[] onEmitted)
    {
        return Wf.Graph(
            ("watch", Wf.ConsumingCall("acme/producer:1.0.0", "poll", onEmitted)),
            ("process", Wf.Http("https://api.example.com/ingest")));
    }

    [Fact]
    public void A_handler_rule_list_that_matches_nothing_takes_the_next_value()
    {
        // A filtering consumer: values it does not care about must be skipped, not fatal. This is
        // the trap the old behaviour set — one uninteresting value silently killed the producer.
        var graph = Consumer([Wf.Dispatch("process", condition: "output.urgent")]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["urgent"] = false },
            ActivityOutcomeKind.Emitted);

        var go = Assert.IsType<TransitionDecision.Resume>(decision);
        Assert.Equal("watch", go.ActivityName);
    }

    [Fact]
    public void An_empty_handler_rule_list_takes_the_next_value()
    {
        var decision = _interpreter.Advance(
            Wf.Session(Consumer([])), "watch", null,
            new Dictionary<string, object>(), ActivityOutcomeKind.Emitted);

        var go = Assert.IsType<TransitionDecision.Resume>(decision);
        Assert.Equal("watch", go.ActivityName);
    }

    [Fact]
    public void A_matching_rule_dispatches_the_document_it_names()
    {
        var graph = Consumer([Wf.Dispatch("process", condition: "output.urgent")]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["urgent"] = true },
            ActivityOutcomeKind.Emitted);

        var dispatch = Assert.IsType<TransitionDecision.Dispatch>(decision);
        Assert.Equal(Wf.SelfKey, dispatch.Workflow);
        Assert.Equal("process", dispatch.StartActivity);
    }

    [Fact]
    public void The_first_matching_rule_wins()
    {
        // Same ordering every other rule list has, and worth pinning here because a dispatch is
        // not a transition and could plausibly have been given "all that match" semantics.
        var graph = Wf.Graph(
            ("watch", Wf.ConsumingCall("acme/producer:1.0.0", "poll",
            [
                Wf.Dispatch("first", condition: "output.urgent"),
                Wf.Dispatch("second")
            ])),
            ("first", Wf.Http("https://api.example.com/first")),
            ("second", Wf.Http("https://api.example.com/second")));

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["urgent"] = true },
            ActivityOutcomeKind.Emitted);

        Assert.Equal("first", Assert.IsType<TransitionDecision.Dispatch>(decision).StartActivity);
    }

    [Fact]
    public void A_rule_with_no_input_transform_hands_the_emitted_value_through()
    {
        // The handler is a separate execution, so the emitted value has to arrive as its run
        // input or it arrives nowhere — there is no shared scope to fall back on.
        var graph = Consumer([Wf.Dispatch("process")]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["n"] = 7 },
            ActivityOutcomeKind.Emitted);

        var dispatch = Assert.IsType<TransitionDecision.Dispatch>(decision);
        Assert.Equal(7, dispatch.Input!["n"]);
    }

    [Fact]
    public void A_transition_rule_leaves_the_loop_for_this_workflows_own_graph()
    {
        // The action that closed the early-stop gap. It is a Goto and not a Resume, which is the
        // whole distinction the driver reads: a Resume keeps the producer running, a Goto ends the
        // subscription and cancels it.
        var graph = Consumer([Wf.EmittedGoto("process", condition: "output.done")]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["done"] = true },
            ActivityOutcomeKind.Emitted);

        var go = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("process", go.ActivityName);
    }

    [Fact]
    public void A_result_rule_ends_the_consumer_with_the_value_it_found()
    {
        // "Find one thing and return it", which before this had to be written as a handler that
        // fails — ending the run as an error and losing the distinction between finishing early on
        // purpose and breaking.
        var graph = Consumer(
        [
            Wf.EmittedResult(
                ValueMap.From([new KeyValuePair<string, object?>("found", "{{ output.id }}")]),
                condition: "output.match")
        ]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["match"] = true, ["id"] = "abc" },
            ActivityOutcomeKind.Emitted);

        var result = Assert.IsType<TransitionDecision.Return>(decision);
        Assert.Equal("abc", Assert.IsType<Dictionary<string, object>>(result.Value)["found"]);
    }

    [Fact]
    public void The_first_matching_rule_wins_whichever_action_it_carries()
    {
        // Rules are ordered, and the ordering is across all three actions rather than within each
        // — a filtering consumer is written as a handler guarded by a condition followed by a
        // catch-all result, and reading them in any other order would invert it.
        var graph = Consumer(
        [
            Wf.Dispatch("process", condition: "output.urgent"),
            Wf.EmittedGoto("process")
        ]);

        var decision = _interpreter.Advance(
            Wf.Session(graph), "watch", null,
            new Dictionary<string, object> { ["urgent"] = false },
            ActivityOutcomeKind.Emitted);

        Assert.IsType<TransitionDecision.Goto>(decision);
    }

    [Fact]
    public void An_ordinary_path_that_runs_out_of_rules_still_ends_the_run()
    {
        // The arm that used to need a dispatch stack to reach. With no handler chains in the
        // consumer's graph there is nothing to distinguish, so this is simply the default again.
        var decision = _interpreter.Advance(
            Wf.Session(Consumer([Wf.Dispatch("process")])), "process", null,
            new Dictionary<string, object>());

        Assert.IsType<TransitionDecision.End>(decision);
    }

    [Fact]
    public void A_failure_still_propagates_rather_than_ending_the_run()
    {
        // The distinction Propagate exists for, unchanged by any of this: on the success path an
        // exhausted list means the run finished, on the failure path it means nobody handled it.
        var decision = _interpreter.Advance(
            Wf.Session(Consumer([Wf.Dispatch("process")])), "process", null, null,
            ActivityOutcomeKind.Failed,
            error: new WorkflowErrorContext("EXECUTION_FAILED", "boom"));

        Assert.IsType<TransitionDecision.Propagate>(decision);
    }
}
