using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// § Scope. Scope values are deep-frozen copies bound non-writable. Every route to changing
/// them is a TypeError (UTOS-E120), the host's data is untouched, and — the other half of the
/// same rule — an expression's own locals are freely mutable, because the freeze decides what is
/// writable, not the method list.
/// </summary>
public class InputsAreFrozenTests
{
    [Theory]
    [InlineData("input.count = 999; input.count")]
    [InlineData("env.SECRET = 'pwned'; env.SECRET")]
    [InlineData("input.user.name = 'pwned'; input.user.name")]
    [InlineData("input.user.extra = 1; input.user.extra")]
    [InlineData("input.items.push(4)")]
    [InlineData("input.items.pop()")]
    [InlineData("input.items.sort()")]
    [InlineData("input.items.reverse()")]
    [InlineData("input.items.splice(0, 1)")]
    [InlineData("input.items.fill(0)")]
    [InlineData("input.items.length = 0; input.items.length")]
    [InlineData("Object.assign(input, { count: 999 }); input.count")]
    [InlineData("Object.defineProperty(input, 'count', { value: 999 })")]
    [InlineData("Object.setPrototypeOf(input, { pwned: 1 })")]
    [InlineData("Array.prototype.map = () => 'pwned'; [1].map(x => x)")]
    [InlineData("Array.prototype.evil = 1; [].evil")]
    [InlineData("String.prototype.trim = () => 'pwned'; ' x '.trim()")]
    public void Every_route_to_mutation_is_a_script_error(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Fact]
    public void The_host_data_is_untouched_by_a_mutation_attempt()
    {
        var scope = Attack.ScopeFrom(Attack.DefaultScope);
        var before = Attack.Json(Attack.Snapshot(scope));
        var engine = Attack.Engine();

        Assert.Throws<Execution.Domain.Templating.TemplateException>(() => engine.RenderTyped("{{ input.count = 999; input.count }}", scope));
        Assert.Throws<Execution.Domain.Templating.TemplateException>(() => engine.RenderTyped("{{ input.items.push(4) }}", scope));

        Assert.Equal(before, Attack.Json(Attack.Snapshot(scope)));
    }

    [Theory]
    [InlineData("const a = []; a.push(1); a.push(2); a", "[1,2]")]
    [InlineData("[3, 1, 2].sort((a, b) => a - b)", "[1,2,3]")]
    [InlineData("const o = { n: 0 }; o.n = 5; o.n", "5")]
    [InlineData("let s = 0; input.items.forEach(x => { s += x; }); s", "6")]
    [InlineData("input.items.reduce((s, x) => s + x, 0)", "6")]
    [InlineData("({ ...input.user, extra: 1 })", """{"name":"x","extra":1}""")]
    public void Locals_are_freely_mutable(string source, string expected) =>
        Assert.Equal(expected, Attack.Json(Attack.Run(source).Returned()));

    [Fact]
    public void An_expression_cannot_change_what_the_next_expression_on_the_same_scope_sees()
    {
        // One engine per scope (per activity decision); each program in its own function scope.
        var scope = Attack.ScopeFrom(Attack.DefaultScope);
        var engine = Attack.Engine();

        Assert.Throws<Execution.Domain.Templating.TemplateException>(() => engine.RenderTyped("{{ input.count = 999 }}", scope));
        Assert.Throws<Execution.Domain.Templating.TemplateException>(() => engine.RenderTyped("{{ Array.prototype.includes = () => 'pwned' }}", scope));
        Assert.Throws<Execution.Domain.Templating.TemplateException>(() => engine.RenderTyped("{{ globalThis.leak = 1 }}", scope));
        engine.RenderTyped("{{ const leaked = 1; leaked }}", scope);

        Assert.Equal(1, engine.RenderTyped("{{ input.count }}", scope));
        Assert.Equal(false, engine.RenderTyped("{{ [1].includes(2) }}", scope));
        Assert.Equal("undefined", engine.RenderTyped("{{ typeof leaked }}", scope));
        Assert.Equal("undefined", engine.RenderTyped("{{ typeof leak }}", scope));
        Assert.Equal("undefined", engine.RenderTyped("{{ typeof globalThis }}", scope));
    }
}
