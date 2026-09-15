using Common.Workflow;
using Execution.Domain.Interpreter;
using UnitTests.Support;

namespace UnitTests.Interpreter;

public class PromiseExpansionTests
{
    private readonly WorkflowInterpreter _interpreter = Wf.Interpreter();

    private static WorkflowGraph GraphWith(params PromiseBranch[] branches)
    {
        return Wf.Graph(
            ("fan", Wf.Promise(PromiseMode.All, 0, branches)),
            ("price", Wf.SubWorkflow("pricing:1.0.0", "quote")),
            ("ship", Wf.SubWorkflow("shipping:1.0.0", "estimate")));
    }

    [Fact]
    public void Static_branches_dispatch_the_documents_they_name()
    {
        var graph = GraphWith(
            Wf.Branch("pricing", "quote", workflow: "pricing:1.0.0"),
            Wf.Branch("shipping", "estimate", workflow: "shipping:1.0.0"));

        var expansion = _interpreter.ExpandPromise(Wf.Session(graph), "fan", null);

        Assert.Equal(PromiseMode.All, expansion.Mode);
        Assert.Equal(2, expansion.Branches.Count);

        Assert.Equal("pricing", expansion.Branches[0].BranchName);
        Assert.Equal("pricing:1.0.0", expansion.Branches[0].WorkflowKey);
        Assert.Equal("quote", expansion.Branches[0].StartActivity);

        Assert.Equal("shipping", expansion.Branches[1].BranchName);
        Assert.Equal("shipping:1.0.0", expansion.Branches[1].WorkflowKey);
        Assert.Equal("estimate", expansion.Branches[1].StartActivity);
    }

    [Fact]
    public void A_branch_whose_condition_is_false_is_not_started()
    {
        var graph = GraphWith(
            Wf.Branch("pricing", "quote", condition: "input.wantPricing"),
            Wf.Branch("shipping", "estimate"));

        var expansion = _interpreter.ExpandPromise(
            Wf.Session(graph), "fan", new Dictionary<string, object> { ["wantPricing"] = false });

        Assert.Equal("shipping", Assert.Single(expansion.Branches).BranchName);
    }

    [Fact]
    public void A_for_each_branch_expands_once_per_item()
    {
        var graph = GraphWith(Wf.Branch(
            "addon", "quote",
            input: Wf.Fields(("sku", "{{ item.sku }}")),
            forEachCollection: "{{ input.addons }}"));

        var session = Wf.Session(graph);
        var addons = new List<object>
        {
            new Dictionary<string, object> { ["sku"] = "a1" },
            new Dictionary<string, object> { ["sku"] = "b2" }
        };

        var expansion = _interpreter.ExpandPromise(
            session, "fan", new Dictionary<string, object> { ["addons"] = addons });

        Assert.Equal(2, expansion.Branches.Count);
        // A literal name renders to itself every time, so the ordinal fallback is what keeps the
        // two expansions apart. Unchanged behaviour, and why forEach needs no migration.
        Assert.Equal("addon_0", expansion.Branches[0].BranchName);
        Assert.Equal("addon_1", expansion.Branches[1].BranchName);
        Assert.Equal("a1", expansion.Branches[0].Input!["sku"]);
        Assert.Equal("b2", expansion.Branches[1].Input!["sku"]);
    }

    [Fact]
    public void A_templated_branch_name_is_rendered_per_item()
    {
        // The proto has always documented `{{ alias.x }}` here, and the daemon has never honoured
        // it: names were built from the raw string plus `_{index}`, so a fixture authoring
        // `addon-{{ item.sku }}` produced the literal key `addon-{{ item.sku }}_0`. Ordinals are
        // gone with it — `addon-SKU-a1` says which expansion failed and `addon_0` does not.
        var graph = GraphWith(Wf.Branch(
            "addon-{{ item.sku }}", "quote",
            forEachCollection: "{{ input.addons }}"));

        var addons = new List<object>
        {
            new Dictionary<string, object> { ["sku"] = "a1" },
            new Dictionary<string, object> { ["sku"] = "b2" }
        };

        var expansion = _interpreter.ExpandPromise(
            Wf.Session(graph), "fan", new Dictionary<string, object> { ["addons"] = addons });

        Assert.Equal(["addon-a1", "addon-b2"], expansion.Branches.Select(b => b.BranchName));
    }

    [Fact]
    public void A_templated_name_that_repeats_falls_back_to_the_ordinal()
    {
        // Within one expansion the ordinal still settles it, exactly as for a literal name. The
        // alternative — failing the promise — would be a run-time error on a document the shared
        // validator accepts, which is the divergence this whole change exists to close.
        var graph = GraphWith(Wf.Branch(
            "addon-{{ item.sku }}", "quote",
            forEachCollection: "{{ input.addons }}"));

        var addons = new List<object>
        {
            new Dictionary<string, object> { ["sku"] = "same" },
            new Dictionary<string, object> { ["sku"] = "same" }
        };

        var expansion = _interpreter.ExpandPromise(
            Wf.Session(graph), "fan", new Dictionary<string, object> { ["addons"] = addons });

        Assert.Equal(["addon-same_0", "addon-same_1"], expansion.Branches.Select(b => b.BranchName));
    }

    [Fact]
    public void A_for_each_branch_over_an_empty_collection_starts_nothing()
    {
        var graph = GraphWith(Wf.Branch("addon", "quote", forEachCollection: "{{ input.addons }}"));

        var expansion = _interpreter.ExpandPromise(
            Wf.Session(graph), "fan", new Dictionary<string, object> { ["addons"] = new List<object>() });

        Assert.Empty(expansion.Branches);
    }

    [Fact]
    public void The_alias_is_scoped_to_the_item_and_does_not_shadow_input_or_env()
    {
        var graph = GraphWith(Wf.Branch(
            "addon", "quote",
            input: Wf.Fields(("sku", "{{ item }}"), ("order", "{{ input.orderId }}"), ("stage", "{{ env.stage }}")),
            forEachCollection: "{{ input.skus }}"));

        var session = Wf.Session(graph, env: new Dictionary<string, string> { ["stage"] = "prod" });

        var expansion = _interpreter.ExpandPromise(session, "fan", new Dictionary<string, object>
        {
            ["orderId"] = "o-1",
            ["skus"] = new List<object> { "a1" }
        });

        var branch = Assert.Single(expansion.Branches);
        Assert.Equal("a1", branch.Input!["sku"]);
        Assert.Equal("o-1", branch.Input["order"]);
        Assert.Equal("prod", branch.Input["stage"]);
    }

    [Fact]
    public void Expanding_the_same_promise_twice_yields_identical_plans()
    {
        // Expansion has no non-deterministic ingredient — no ids, no clock — which is what
        // lets the driver treat its result as replayable.
        var graph = GraphWith(
            Wf.Branch("pricing", "quote", workflow: "pricing:1.0.0"),
            Wf.Branch("shipping", "estimate", workflow: "shipping:1.0.0"));
        var session = Wf.Session(graph);

        var first = _interpreter.ExpandPromise(session, "fan", null);
        var second = _interpreter.ExpandPromise(session, "fan", null);

        Assert.Equal(first.Branches, second.Branches);
    }

    [Fact]
    public void A_branch_may_dispatch_the_document_it_is_declared_in()
    {
        // What `workflow: self` becomes once the CLI has resolved it. Recursive and nested fan-out
        // rest on this: a branch may re-enter the promise activity itself, and the cycle check
        // never sees it because self never enters the dependency graph.
        var graph = Wf.Graph(
            ("fan", Wf.Promise(PromiseMode.All, 0, [Wf.Branch("pricing", "fetch")])),
            ("fetch", Wf.Http("https://example.com")));

        var expansion = _interpreter.ExpandPromise(Wf.Session(graph), "fan", null);

        var branch = Assert.Single(expansion.Branches);
        Assert.Equal("pricing", branch.BranchName);
        Assert.Equal("test:1.0.0", branch.WorkflowKey);
        Assert.Equal("fetch", branch.StartActivity);
    }

    // This suite used to own both "every documented mode parses" and "an unknown mode is
    // rejected", because the mode arrived here as a string. It is a oneof on the wire now: an
    // unknown mode is unrepresentable, and choosing the member happens at the mapping boundary.
    // Both concerns moved to WorkflowMappingTests. What is left to check here is that expansion
    // carries the mode through untouched.
    [Theory]
    [InlineData(PromiseMode.All)]
    [InlineData(PromiseMode.Any)]
    [InlineData(PromiseMode.Race)]
    [InlineData(PromiseMode.Count)]
    public void Expansion_carries_the_mode_through(PromiseMode mode)
    {
        var graph = Wf.Graph(
            ("fan", Wf.Promise(mode, 1, [Wf.Branch("pricing", "price")])),
            ("price", Wf.SubWorkflow("pricing:1.0.0", "quote")));

        Assert.Equal(mode, _interpreter.ExpandPromise(Wf.Session(graph), "fan", null).Mode);
    }
}
