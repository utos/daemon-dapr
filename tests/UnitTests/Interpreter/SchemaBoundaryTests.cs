using Common.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Schemas;
using UnitTests.Support;
using WorkflowDocument = Utos.Workflows.V1.WorkflowDocument;

namespace UnitTests.Interpreter;

/// <summary>
/// The run-time schema boundaries, <c>UTOS-H103</c>–<c>UTOS-H106</c> of
/// <c>api/docs/workflow-schemas.md</c>. Whether a value satisfies a schema is settled by the
/// conformance corpus; what these pin is that the interpreter applies the right declaration at
/// the right moment, and carries the right code when it does not.
/// </summary>
public class SchemaBoundaryTests
{
    /// <summary>A compiled object schema requiring one string property, closed.</summary>
    private static ValueMap Requires(string property) => Wf.Fields(
        ("type", "object"),
        ("required", new object[] { property }),
        ("properties", Wf.Fields((property, Wf.Fields(("type", "string"))))),
        ("unevaluatedProperties", false));

    [Fact]
    public void A_transition_input_that_the_target_refuses_fails_the_run()
    {
        var graph = Wf.Graph(
            ("start", Wf.Http("https://example.test", onSuccess:
                Wf.Goto("next", Wf.Fields(("wrong", "x"))))),
            ("next", new WorkflowActivity(
                new TimerActivityConfig("1s"),
                Schema: new ActivitySchema(Requires("orderId")))));

        var error = Assert.Throws<SchemaValidationFailedException>(() =>
            Wf.Interpreter().Advance(Wf.Session(graph), "start", null, new Dictionary<string, object>()));

        Assert.Equal(SchemaValidation.TransitionInput, error.Code);
        Assert.Equal(SchemaBoundary.Input, error.Boundary);
    }

    [Fact]
    public void A_transition_fills_the_targets_declared_defaults()
    {
        // The filled value is what travels on, so the next activity — and a replay — sees it.
        var schema = Wf.Fields(
            ("type", "object"),
            ("properties", Wf.Fields(
                ("quality", Wf.Fields(("type", "integer"), ("default", 80))))),
            ("unevaluatedProperties", false));

        var graph = Wf.Graph(
            ("start", Wf.Http("https://example.test", onSuccess: Wf.Goto("next", Wf.Fields()))),
            ("next", new WorkflowActivity(
                new TimerActivityConfig("1s"),
                Schema: new ActivitySchema(schema))));

        var decision = Wf.Interpreter()
            .Advance(Wf.Session(graph), "start", null, new Dictionary<string, object>());

        var goto_ = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal(80L, Convert.ToInt64(goto_.Input!["quality"]));
    }

    [Fact]
    public void A_declared_output_that_the_result_violates_fails_the_run()
    {
        var graph = Wf.Graph(("start", Wf.Http("https://example.test",
            onSuccess: Wf.Result(Wf.Fields(("delivered", "yes"))))));

        var workflow = graph.Workflow with
        {
            Spec = graph.Workflow.Spec with { Output = Requires("receiptId") }
        };

        var session = Wf.Session(new WorkflowGraph(workflow, graph.WorkflowKey));

        var error = Assert.Throws<SchemaValidationFailedException>(() =>
            Wf.Interpreter().Advance(session, "start", null, new Dictionary<string, object>()));

        Assert.Equal(SchemaValidation.Result, error.Code);
        Assert.Equal(SchemaBoundary.Output, error.Boundary);
    }

    [Fact]
    public void A_declared_emission_that_an_emit_violates_fails_the_run()
    {
        var graph = Wf.Graph(
            ("start", Wf.Http("https://example.test",
                onSuccess: Wf.Emit(Wf.Fields(("wrong", "x")), "start"))));

        var workflow = graph.Workflow with
        {
            Spec = graph.Workflow.Spec with { Emits = Requires("messageId") }
        };

        var session = Wf.Session(new WorkflowGraph(workflow, graph.WorkflowKey));

        var error = Assert.Throws<SchemaValidationFailedException>(() =>
            Wf.Interpreter().Advance(session, "start", null, new Dictionary<string, object>()));

        Assert.Equal(SchemaValidation.EmittedValue, error.Code);
        Assert.Equal(SchemaBoundary.Emits, error.Boundary);
    }

    [Fact]
    public void An_invocation_input_that_the_callee_refuses_fails_the_calling_activity()
    {
        // UTOS-H104 is the one run-time boundary an onFailure rule may catch, because it is the
        // calling activity failing rather than the run being abandoned mid-transition.
        var session = SessionCallingChild(Wf.Fields(("wrong", "x")));

        var error = Assert.Throws<SchemaValidationFailedException>(() =>
            Wf.Interpreter().ResolveInput(session, "call", null));

        Assert.Equal(SchemaValidation.InvocationInput, error.Code);
    }

    [Fact]
    public void An_invocation_the_callee_accepts_passes_through()
    {
        var session = SessionCallingChild(Wf.Fields(("orderId", "A-1")));

        var resolved = Wf.Interpreter().ResolveInput(session, "call", null);

        var sub = Assert.IsType<ResolvedActivityInput.SubWorkflow>(resolved);
        Assert.Equal("A-1", sub.Input!["orderId"]);
    }

    [Fact]
    public void A_failure_carries_the_boundary_and_every_failing_location_in_its_details()
    {
        // This is what `error.details.schema` reads as from an onFailure rule, so a caller can
        // branch on which field was wrong rather than on message text.
        var session = SessionCallingChild(Wf.Fields(("wrong", "x")));

        var error = Assert.Throws<SchemaValidationFailedException>(() =>
            Wf.Interpreter().ResolveInput(session, "call", null));

        var schema = Assert.IsType<Dictionary<string, object>>(error.ToDetails()["schema"]);

        Assert.Equal("input", schema["boundary"]);
        Assert.Equal(0, schema["truncated"]);

        var errors = Assert.IsType<List<object>>(schema["errors"]);
        var reported = errors.Cast<Dictionary<string, object>>()
            .Select(e => ((string)e["instanceLocation"], (string)e["keyword"]))
            .ToList();

        // Both halves: the required property that is missing, and the one that is not declared.
        Assert.Contains(("", "required"), reported);
        Assert.Contains(("/wrong", "unevaluatedProperties"), reported);
    }

    [Fact]
    public void A_workflow_that_declares_nothing_is_untouched()
    {
        // The path every workflow written before schemas existed takes, and the reason this
        // feature needed no migration.
        var graph = Wf.Graph(
            ("start", Wf.Http("https://example.test", onSuccess: Wf.Goto("next", Wf.Fields(("any", "thing"))))),
            ("next", Wf.Timer("1s")));

        var decision = Wf.Interpreter()
            .Advance(Wf.Session(graph), "start", null, new Dictionary<string, object>());

        var goto_ = Assert.IsType<TransitionDecision.Goto>(decision);
        Assert.Equal("thing", goto_.Input!["any"]);
    }

    /// <summary>
    /// A session whose bundle holds two documents: the caller, and a child whose start activity
    /// requires <c>orderId</c>. Two are needed because an invocation is checked against the
    /// <em>callee's</em> declaration, which a single-document session cannot reach.
    /// </summary>
    private static WorkflowSession SessionCallingChild(ValueMap input)
    {
        const string childKey = "child:1.0.0";

        var caller = Wf.Graph(("call", Wf.SubWorkflow(childKey, "receive", input))).Workflow;

        var child = new Workflow(
            WorkflowDocument.ApiVersion,
            WorkflowDocument.Kind,
            new WorkflowMetadata("child", "1.0.0"),
            new WorkflowSpec(new Dictionary<string, WorkflowActivity>
            {
                ["receive"] = new(
                    new TimerActivityConfig("1s"),
                    Schema: new ActivitySchema(Requires("orderId")))
            }));

        var bundle = new WorkflowBundle(Wf.SelfKey, new Dictionary<string, Workflow>
        {
            [Wf.SelfKey] = caller,
            [childKey] = child
        });

        return new WorkflowSession(
            new WorkflowGraph(caller, Wf.SelfKey),
            new Dictionary<string, object>(),
            new Dictionary<string, string>(),
            bundle);
    }
}
