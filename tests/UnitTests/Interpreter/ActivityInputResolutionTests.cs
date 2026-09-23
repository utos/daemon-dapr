using Common.Workflow;
using Execution.Domain.Templating;
using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

public class ActivityInputResolutionTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    [Fact]
    public void An_http_url_is_rendered_from_input_and_env()
    {
        var graph = Wf.Graph(("a", Wf.Http("{{ env.base }}/users/{{ input.id }}")));
        var session = Wf.Session(graph, env: new Dictionary<string, string> { ["base"] = "https://api.example.com" });

        var resolved = (ResolvedActivityInput.Http)_interpreter.ResolveInput(
            session, "a", new Dictionary<string, object> { ["id"] = 42 });

        Assert.Equal("https://api.example.com/users/42", resolved.Url);
        Assert.Equal("GET", resolved.Method);
    }

    [Fact]
    public void Headers_are_rendered()
    {
        var graph = Wf.Graph(("a", Wf.Http(
            "https://example.com",
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {{ env.token }}" })));

        var session = Wf.Session(graph, env: new Dictionary<string, string> { ["token"] = "s3cret" });

        var resolved = (ResolvedActivityInput.Http)_interpreter.ResolveInput(session, "a", null);

        Assert.Equal("Bearer s3cret", resolved.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public void A_body_is_rendered_for_methods_that_carry_one(string method)
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com", method, body: "{\"id\":{{ input.id }}}")));
        var session = Wf.Session(graph);

        var resolved = (ResolvedActivityInput.Http)_interpreter.ResolveInput(
            session, "a", new Dictionary<string, object> { ["id"] = 7 });

        Assert.Equal("{\"id\":7}", resolved.Body);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public void A_body_is_dropped_for_methods_that_do_not(string method)
    {
        var graph = Wf.Graph(("a", Wf.Http("https://example.com", method, body: "ignored")));

        var resolved = (ResolvedActivityInput.Http)_interpreter.ResolveInput(Wf.Session(graph), "a", null);

        Assert.Null(resolved.Body);
    }

    [Fact]
    public void A_url_that_renders_into_something_unusable_is_rejected_by_name()
    {
        // Passes the load-time check because the scheme comes from env; fails once rendered.
        var graph = Wf.Graph(("fetch-user", Wf.Http("{{ env.base }}/users")));
        var session = Wf.Session(graph, env: new Dictionary<string, string> { ["base"] = "not a url" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => _interpreter.ResolveInput(session, "fetch-user", null));

        Assert.StartsWith("Activity 'fetch-user' resolved to an unusable URL 'not a url/users'", ex.Message);
    }

    [Fact]
    public void A_sub_workflow_call_without_a_transform_passes_its_input_through()
    {
        var graph = Wf.Graph(("call", Wf.SubWorkflow("emailer:1.0.0", "send")));
        var input = new Dictionary<string, object> { ["to"] = "a@example.com" };

        var resolved = (ResolvedActivityInput.SubWorkflow)_interpreter.ResolveInput(
            Wf.Session(graph), "call", input);

        Assert.Equal("emailer:1.0.0", resolved.WorkflowKey);
        Assert.Equal("send", resolved.StartActivity);
        Assert.Same(input, resolved.Input);
        Assert.False(resolved.Detached);
    }

    [Fact]
    public void A_sub_workflow_call_with_a_transform_renders_it()
    {
        var graph = Wf.Graph(("call", Wf.SubWorkflow(
            "emailer:1.0.0", "send", Wf.Fields(("recipient", "{{ input.to }}")))));

        var resolved = (ResolvedActivityInput.SubWorkflow)_interpreter.ResolveInput(
            Wf.Session(graph), "call", new Dictionary<string, object> { ["to"] = "a@example.com" });

        Assert.NotNull(resolved.Input);
        Assert.Equal("a@example.com", resolved.Input["recipient"]);
    }

    [Fact]
    public void A_detached_sub_workflow_call_is_marked_as_such()
    {
        var graph = Wf.Graph(("call", Wf.SubWorkflow("emailer:1.0.0", "send", spawn: true)));

        var resolved = (ResolvedActivityInput.SubWorkflow)_interpreter.ResolveInput(
            Wf.Session(graph), "call", null);

        Assert.True(resolved.Detached);
    }

    [Fact]
    public void An_activity_kind_with_nothing_to_resolve_is_rejected()
    {
        // A promise resolves through ExpandPromise instead: its branches are conditioned and
        // fanned out, which is a different question from rendering one configuration.
        var graph = Wf.Graph(("fan", Wf.Promise(PromiseMode.All, 0, [Wf.Branch("one", "a")])));

        var ex = Assert.Throws<NotSupportedException>(
            () => _interpreter.ResolveInput(Wf.Session(graph), "fan", null));

        Assert.Equal("Unsupported activity config type 'Promise' for activity 'fan'", ex.Message);
    }

    [Fact]
    public void A_timer_resolves_its_duration_from_the_unit_shorthand()
    {
        // Spec 0.20: the field is a string, and what it means is the spec's grammar rather than
        // this daemon's reading of it.
        var graph = Wf.Graph(("wait", Wf.Timer("1h30m")));

        var resolved = Assert.IsType<ResolvedActivityInput.Timer>(
            _interpreter.ResolveInput(Wf.Session(graph), "wait", null));

        Assert.Equal(TimeSpan.FromMinutes(90), resolved.Duration);
    }

    [Fact]
    public void A_timers_duration_may_be_a_whole_field_template()
    {
        // Which is why it is resolved here at all, rather than parsed at the mapping boundary:
        // there is no duration until the run renders one.
        var graph = Wf.Graph(("wait", Wf.Timer("{{ input.backoff }}")));
        var session = Wf.Session(graph);

        var resolved = Assert.IsType<ResolvedActivityInput.Timer>(
            _interpreter.ResolveInput(
                session, "wait", new Dictionary<string, object> { ["backoff"] = "45s" }));

        Assert.Equal(TimeSpan.FromSeconds(45), resolved.Duration);
    }

    [Theory]
    [InlineData("soon")]                // not a duration at all
    [InlineData("90")]                  // a number says neither seconds nor hours
    [InlineData("0s")]                  // well formed, and nothing waits zero
    public void A_duration_template_that_renders_to_something_else_fails_the_run(string rendered)
    {
        // UTOS-E106, and a run-time failure rather than a load-time one for the reason a templated
        // URL is: the value did not exist until the run produced it.
        var graph = Wf.Graph(("wait", Wf.Timer("{{ input.backoff }}")));

        var ex = Assert.Throws<TemplateException>(
            () => _interpreter.ResolveInput(
                Wf.Session(graph), "wait",
                new Dictionary<string, object> { ["backoff"] = rendered }));

        Assert.Equal("UTOS-E106", ex.Code);
        Assert.Contains("wait", ex.Message);
    }
}
