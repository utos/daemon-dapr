using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

/// <summary>
/// The <c>response</c> context is what lets a rule branch on an HTTP status without an error-code
/// taxonomy — <c>on_failure</c> distinguishing a 429 from a 401 is the motivating case.
/// </summary>
public class ResponseContextTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    private static HttpResponseContext Response(
        int status, params (string Name, string Value)[] headers)
    {
        return new HttpResponseContext(
            status,
            headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase),
            Common.Workflow.WorkflowBlob.Inline(System.Text.Encoding.UTF8.GetBytes("{}"), "application/json"));
    }

    private TransitionDecision Advance(HttpResponseContext? response, params Common.Workflow.TransitionRule[] rules)
    {
        var graph = Wf.Graph(
            ("call", Wf.Http("https://api.example.com/thing", onSuccess: rules)),
            ("retry", Wf.Http("https://api.example.com/thing")),
            ("reauth", Wf.Http("https://api.example.com/token")),
            ("fallback", Wf.Http("https://api.example.com/other")));

        return _interpreter.Advance(
            Wf.Session(graph), "call", null, new Dictionary<string, object>(),
            ActivityOutcomeKind.Succeeded, response);
    }

    [Fact]
    public void A_rule_can_branch_on_the_status_code()
    {
        var decision = Advance(
            Response(429),
            Wf.Goto("retry", condition: "response.status === 429"),
            Wf.Goto("fallback"));

        Assert.Equal("retry", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void A_non_matching_status_falls_through_to_the_next_rule()
    {
        var decision = Advance(
            Response(401),
            Wf.Goto("retry", condition: "response.status === 429"),
            Wf.Goto("reauth", condition: "response.status === 401"),
            Wf.Goto("fallback"));

        Assert.Equal("reauth", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void A_header_is_readable_when_it_is_present()
    {
        var decision = Advance(
            Response(429, ("Retry-After", "120")),
            Wf.Goto("retry", condition: "response.headers['retry-after'] === '120'"),
            Wf.Goto("fallback"));

        Assert.Equal("retry", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void Status_is_comparable_even_when_there_was_no_response_at_all()
    {
        // The case that would otherwise break failure routing: a timeout, a blocked destination, or
        // a non-HTTP activity has no response, and the engine throws on a *missing member*, not
        // merely on an undefined root. Carrying the keys with null values is what makes this
        // evaluate false instead of failing while a failure is already being handled.
        var decision = Advance(
            null,
            Wf.Goto("retry", condition: "response.status === 429"),
            Wf.Goto("fallback"));

        Assert.Equal("fallback", Assert.IsType<TransitionDecision.Goto>(decision).ActivityName);
    }

    [Fact]
    public void An_absent_response_still_exposes_every_key()
    {
        var scoped = HttpResponseContext.ToScopeValue(null);

        // Every key `response` has is present even when there was no response at all, so
        // `response.status === 429` is false rather than a ReferenceError raised while a failure is
        // already being handled.
        Assert.Null(scoped["status"]);
        Assert.Null(scoped["body"]);
        Assert.NotNull(scoped["headers"]);

        // `bodyText` is *absent*, not null. It was retired in spec 0.20, and the scope converter
        // installs a getter that throws for it — a null here would read as an empty body, which is
        // the silent wrong answer the retirement exists to prevent.
        Assert.False(scoped.ContainsKey("bodyText"));
    }

    [Fact]
    public void Headers_are_matched_without_regard_to_case()
    {
        var scoped = HttpResponseContext.ToScopeValue(Response(200, ("Retry-After", "5")));
        var headers = Assert.IsAssignableFrom<Dictionary<string, object?>>(scoped["headers"]);

        Assert.Equal("5", headers["retry-after"]);
    }
}
