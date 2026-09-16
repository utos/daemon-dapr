using Common.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Templating;
using UnitTests.Support;

namespace UnitTests.Interpreter;

public class StructRendererTests
{
    private readonly StructRenderer _renderer = new(Wf.Engine());

    private static TemplateScope Scope(params (string Key, object Value)[] input)
    {
        return new TemplateScope()
            .WithInput(input.ToDictionary(f => f.Key, f => f.Value))
            .WithEnv(new Dictionary<string, string>());
    }

    [Fact]
    public void A_whole_expression_keeps_its_native_type()
    {
        // Rendering to a string here would turn the next hop's `+` into concatenation, which
        // is what breaks a counter threaded across a loop's back-edge.
        var rendered = _renderer.Render(
            Wf.Fields(("next", "{{ input.count + 1 }}")), Scope(("count", 41)));

        Assert.Equal(42, rendered["next"]);
    }

    [Fact]
    public void Interpolation_around_an_expression_yields_text()
    {
        var rendered = _renderer.Render(
            Wf.Fields(("label", "user-{{ input.id }}")), Scope(("id", 7)));

        Assert.Equal("user-7", rendered["label"]);
    }

    [Fact]
    public void A_literal_with_no_expression_passes_through()
    {
        var rendered = _renderer.Render(Wf.Fields(("kind", "order")), Scope());

        Assert.Equal("order", rendered["kind"]);
    }

    [Fact]
    public void Non_string_leaves_are_converted_without_rendering()
    {
        var rendered = _renderer.Render(
            Wf.Fields(("retries", 3), ("ratio", 1.5), ("enabled", true)), Scope());

        Assert.Equal(3, rendered["retries"]);
        Assert.Equal(1.5, rendered["ratio"]);
        Assert.Equal(true, rendered["enabled"]);
    }

    [Fact]
    public void Nested_structs_are_rendered_recursively()
    {
        var rendered = _renderer.Render(
            Wf.Fields(("order", Wf.Fields(("id", "{{ input.id }}"), ("note", "for {{ input.who }}")))),
            Scope(("id", 12), ("who", "ana")));

        var order = Assert.IsType<Dictionary<string, object>>(rendered["order"]);
        Assert.Equal(12, order["id"]);
        Assert.Equal("for ana", order["note"]);
    }

    [Fact]
    public void Lists_are_rendered_element_by_element()
    {
        var rendered = _renderer.Render(
            Wf.Fields(("ids", new object[] { "{{ input.a }}", "{{ input.b }}", "static" })),
            Scope(("a", 1), ("b", 2)));

        var ids = Assert.IsType<List<object?>>(rendered["ids"]);
        Assert.Equal([1, 2, "static"], ids);
    }

    [Fact]
    public void A_field_that_renders_to_nothing_is_dropped()
    {
        var rendered = _renderer.Render(Wf.Fields(("missing", (object?)null)), Scope());

        Assert.DoesNotContain("missing", rendered.Keys);
    }
}
