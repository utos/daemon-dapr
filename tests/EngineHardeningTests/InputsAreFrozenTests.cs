using EngineHardeningTests.Support;
using Jint.Runtime;

namespace EngineHardeningTests;

/// <summary>
/// §6.3. Scope roots are deep-frozen and bound non-writable. Every route to changing them is a
/// TypeError, the host's data is untouched, and — the other half of the same rule — an expression's
/// own locals are freely mutable, because the freeze decides what is writable, not the method list.
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
    [InlineData("globalThis.input = { count: 999 }; input.count")]
    public void Every_route_to_mutating_an_input_is_a_TypeError(string source) =>
        Attack.Run(source).Threw<JavaScriptException>();

    [Theory]
    [InlineData("Object.defineProperty(input, 'count', { value: 999 })")]
    [InlineData("Object.setPrototypeOf(input, { pwned: 1 })")]
    public void Reflection_style_mutators_do_not_exist(string source) =>
        Assert.Contains("is not a function", Attack.Run(source).Threw<JavaScriptException>().Message);

    [Theory]
    [InlineData("Array.prototype.map = () => 'pwned'; [1].map(x => x)")]
    [InlineData("Array.prototype.evil = 1; [].evil")]
    [InlineData("String.prototype.trim = () => 'pwned'; ' x '.trim()")]
    public void Built_in_prototypes_are_frozen(string source) =>
        Attack.Run(source).Threw<JavaScriptException>();

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
    public void An_expression_cannot_change_what_the_next_expression_on_the_same_engine_sees()
    {
        var evaluator = new ReferenceEvaluator();
        using var session = evaluator.Open(Attack.ScopeFrom(Attack.DefaultScope));

        Assert.Throws<JavaScriptException>(() => session.Evaluate("input.count = 999"));
        Assert.Throws<JavaScriptException>(() => session.Evaluate("Array.prototype.includes = () => 'pwned'"));

        Assert.Equal(1, session.Evaluate("input.count"));
        Assert.Equal(false, session.Evaluate("[1].includes(2)"));
    }

    [Fact]
    public void The_global_object_is_not_reachable_so_nothing_can_be_left_behind()
    {
        // `var` and `function` declarations are outside the language, so the global object has no
        // legitimate use, is not named, and is frozen; `globalThis.leak = 1` was the last way one
        // expression could change what the next one sees.
        using var session = new ReferenceEvaluator().Open(Attack.ScopeFrom(Attack.DefaultScope));

        Assert.Throws<JavaScriptException>(() => session.Evaluate("globalThis.leak = 1"));
        Assert.Equal("undefined", session.Evaluate("typeof leak"));
        Assert.Equal("undefined", session.Evaluate("typeof globalThis"));
    }
}
