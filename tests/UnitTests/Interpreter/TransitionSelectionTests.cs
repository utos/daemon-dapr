using Common.Workflow;
using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

public class TransitionSelectionTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    [Fact]
    public void An_activity_with_no_transitions_ends_the_path()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com"))));

        var decision = _interpreter.Advance(session, "a", null, Output(("status", 200)));

        Assert.IsType<TransitionDecision.End>(decision);
    }

    [Fact]
    public void The_first_rule_with_a_matching_condition_wins()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess:
            [
                Wf.Goto("small", condition: "{{ output.count < 10 }}"),
                Wf.Goto("medium", condition: "{{ output.count < 100 }}"),
                Wf.Goto("large")
            ]))));

        var decision = _interpreter.Advance(session, "a", null, Output(("count", 50)));

        Assert.Equal("medium", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void An_unconditional_rule_is_the_fallback_when_nothing_matches()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess:
            [
                Wf.Goto("hit", condition: "{{ output.count > 10 }}"),
                Wf.Goto("fallback")
            ]))));

        var decision = _interpreter.Advance(session, "a", null, Output(("count", 1)));

        Assert.Equal("fallback", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void No_rule_matching_ends_the_path()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess: [Wf.Goto("never", condition: "{{ output.count > 10 }}")]))));

        var decision = _interpreter.Advance(session, "a", null, Output(("count", 1)));

        Assert.IsType<TransitionDecision.End>(decision);
    }

    [Fact]
    public void A_result_rule_returns_its_rendered_value()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess: [Wf.Result(Wf.Fields(("total", "{{ output.count }}"), ("label", "n={{ output.count }}")))]))));

        var decision = _interpreter.Advance(session, "a", null, Output(("count", 7)));

        var value = Assert.IsType<Dictionary<string, object>>(
            Assert.IsType<TransitionDecision.Return>(decision).Value);

        // A whole expression keeps its native type; interpolation is text.
        Assert.Equal(7, value["total"]);
        Assert.Equal("n=7", value["label"]);
    }

    [Fact]
    public void A_transition_without_an_input_transform_passes_the_output_through()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess: [Wf.Goto("b")]))));

        var output = Output(("count", 3));

        var decision = _interpreter.Advance(session, "a", null, output);

        Assert.Same(output, Assert.IsType<TransitionDecision.Goto>(decision).Input);
    }

    [Fact]
    public void A_transition_with_an_input_transform_renders_it_from_input_and_output()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess:
            [
                Wf.Goto("b", Wf.Fields(
                    ("next", "{{ output.count + 1 }}"),
                    ("carried", "{{ input.seed }}")))
            ]))));

        var decision = _interpreter.Advance(
            session, "a", Input(("seed", "abc")), Output(("count", 41)));

        var mapped = Assert.IsType<TransitionDecision.Goto>(decision).Input;
        Assert.NotNull(mapped);
        Assert.Equal(42, mapped["next"]);
        Assert.Equal("abc", mapped["carried"]);
    }

    [Fact]
    public void A_timer_passes_its_input_through_as_its_output()
    {
        var session = Wf.Session(Wf.Graph(("wait", Wf.Timer(TimeSpan.FromSeconds(1),
            Wf.Goto("poll", Wf.Fields(("attempt", "{{ output.attempt + 1 }}")))))));

        // A timer does no work, so the driver hands the interpreter no output at all.
        var decision = _interpreter.Advance(session, "wait", Input(("attempt", 2)), null);

        var mapped = Assert.IsType<TransitionDecision.Goto>(decision).Input;
        Assert.NotNull(mapped);
        Assert.Equal(3, mapped["attempt"]);
    }

    [Fact]
    public void A_timer_used_as_the_start_activity_falls_back_to_the_run_input()
    {
        var graph = Wf.Graph(("wait", Wf.Timer(TimeSpan.FromSeconds(1),
            Wf.Goto("poll", Wf.Fields(("seen", "{{ output.attempt }}"))))));

        var session = Wf.Session(graph, runInput: new Dictionary<string, object> { ["attempt"] = 9 });

        var decision = _interpreter.Advance(session, "wait", null, null);

        var mapped = Assert.IsType<TransitionDecision.Goto>(decision).Input;
        Assert.NotNull(mapped);
        Assert.Equal(9, mapped["seen"]);
    }

    [Fact]
    public void Conditions_can_read_the_runs_ambient_env()
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com",
            onSuccess:
            [
                Wf.Goto("prod", condition: "{{ env.stage == 'production' }}"),
                Wf.Goto("staging")
            ])));

        var session = Wf.Session(graph, env: new Dictionary<string, string> { ["stage"] = "production" });

        var decision = _interpreter.Advance(session, "a", null, Output(("count", 1)));

        Assert.Equal("prod", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void An_unknown_activity_is_reported_with_the_workflow_it_was_looked_for_in()
    {
        var session = Wf.Session(Wf.Graph(("a", Wf.Http("https://example.com"))));

        var ex = Assert.Throws<InvalidOperationException>(
            () => _interpreter.Advance(session, "ghost", null, null));

        Assert.Equal("Activity 'ghost' not found in workflow 'test:1.0.0'", ex.Message);
    }

    [Fact]
    public void A_rule_with_no_action_is_rejected()
    {
        var activity = Wf.Http("https://example.com", onSuccess: [new TransitionRule()]);

        var session = Wf.Session(Wf.Graph(("a", activity)));

        var ex = Assert.Throws<InvalidOperationException>(
            () => _interpreter.Advance(session, "a", null, null));

        Assert.Equal("Transition rule has no action set (neither transition nor result)", ex.Message);
    }

    private static Dictionary<string, object> Output(params (string Key, object Value)[] fields)
    {
        return fields.ToDictionary(f => f.Key, f => f.Value);
    }

    private static Dictionary<string, object> Input(params (string Key, object Value)[] fields)
    {
        return fields.ToDictionary(f => f.Key, f => f.Value);
    }
}
