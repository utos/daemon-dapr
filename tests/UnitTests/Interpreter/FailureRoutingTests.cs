using Common.Workflow;
using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

/// <summary>
/// <c>on_failure</c> routing. The propagate cases matter most: on an ordinary success path an
/// unmatched rule list means the run finished, and on the failure path it must mean the opposite.
///
/// It is no longer the only path that reads an exhausted list its own way — a handler consuming a
/// sub-workflow's output goes back for the next value instead. See
/// <see cref="HandlerResumeTests"/>, which asserts the case where the two meet: a failure inside a
/// handler propagates rather than resuming.
/// </summary>
public class FailureRoutingTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    private static WorkflowActivity Failing(
        TransitionRule[]? onFailure = null, params TransitionRule[] onSuccess)
    {
        return new WorkflowActivity(
            new HttpActivityConfig("GET", "https://api.example.com/thing", null, null),
            onSuccess,
            onFailure ?? []);
    }

    private TransitionDecision Advance(
        WorkflowActivity activity,
        HttpResponseContext? response = null,
        WorkflowErrorContext? error = null,
        IReadOnlyDictionary<string, object>? activityInput = null,
        IReadOnlyDictionary<string, object>? runInput = null)
    {
        var graph = Wf.Graph(
            ("call", activity),
            ("backoff", Wf.Http("https://api.example.com/thing")),
            ("reauth", Wf.Http("https://api.example.com/token")),
            ("giveup", Wf.Http("https://api.example.com/other")));

        return _interpreter.Advance(
            Wf.Session(graph, runInput), "call", activityInput, null,
            ActivityOutcomeKind.Failed, response,
            error ?? new WorkflowErrorContext("EXECUTION_FAILED", "boom"));
    }

    [Fact]
    public void An_unmatched_failure_propagates_rather_than_completing_the_run()
    {
        // The single most important assertion here. If this returns End, an unhandled failure is
        // reported as a successful completion — a regression no other test would notice.
        var decision = Advance(Failing(
            onFailure: [Wf.Goto("backoff", condition: "{{ response.status == 429 }}")]),
            response: new HttpResponseContext(500, [], null));

        Assert.IsType<TransitionDecision.Propagate>(decision);
    }

    [Fact]
    public void An_empty_failure_list_propagates()
    {
        Assert.IsType<TransitionDecision.Propagate>(Advance(Failing()));
    }

    [Fact]
    public void An_unmatched_success_list_still_ends_the_run()
    {
        // The asymmetry, from the other side: same situation on the success path means finished.
        var graph = Wf.Graph(("call", Wf.Http("https://api.example.com/thing",
            onSuccess: Wf.Goto("call", condition: "{{ output.ready }}"))));

        var decision = _interpreter.Advance(
            Wf.Session(graph), "call", null,
            new Dictionary<string, object> { ["ready"] = false });

        Assert.IsType<TransitionDecision.End>(decision);
    }

    [Fact]
    public void A_failure_rule_matching_on_status_wins()
    {
        var decision = Advance(Failing(
            onFailure:
            [
                Wf.Goto("backoff", condition: "{{ response.status == 429 }}"),
                Wf.Goto("reauth", condition: "{{ response.status == 401 }}"),
                Wf.Goto("giveup")
            ]),
            response: new HttpResponseContext(401, [], null));

        Assert.Equal("reauth", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void A_rule_with_no_condition_is_the_failure_fallback()
    {
        var decision = Advance(Failing(
            onFailure: [Wf.Goto("backoff", condition: "{{ response.status == 429 }}"), Wf.Goto("giveup")]),
            response: new HttpResponseContext(500, [], null));

        Assert.Equal("giveup", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void Success_rules_are_not_consulted_on_the_failure_path()
    {
        var decision = Advance(Failing(
            onFailure: [Wf.Goto("giveup")],
            onSuccess: Wf.Goto("backoff")));

        Assert.Equal("giveup", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void The_error_message_is_readable_from_a_condition()
    {
        var decision = Advance(Failing(
            onFailure: [Wf.Goto("backoff", condition: "{{ error.message == 'boom' }}"), Wf.Goto("giveup")]));

        Assert.Equal("backoff", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void A_failure_handler_receives_the_failing_activitys_input_not_the_run_input()
    {
        // Falling through to the activity's null output would make InputFor substitute the run
        // input, silently rewinding the handler to the state the workflow started in.
        var decision = Advance(
            Failing(onFailure: [Wf.Goto("giveup")]),
            activityInput: new Dictionary<string, object> { ["cursor"] = "abc" },
            runInput: new Dictionary<string, object> { ["cursor"] = "start" });

        var target = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("abc", target.Input!["cursor"]);
    }

    [Fact]
    public void A_failure_rule_can_still_return_a_result()
    {
        var decision = Advance(Failing(
            onFailure: [Wf.Result(Wf.Fields(("handled", true)))]));

        var returned = Assert.IsType<TransitionDecision.Return>(decision);
        Assert.Equal(true, Assert.IsAssignableFrom<Dictionary<string, object>>(returned.Value)["handled"]);
    }
}
