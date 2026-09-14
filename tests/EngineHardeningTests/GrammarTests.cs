using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// §6.0. The grammar is the language definition — an allow-list of syntax-tree node types,
/// checked by the daemon on the tree it is about to run. Two properties: every construct that
/// manufactures iteration, reaches code, or builds a prototype chain is refused before the engine
/// sees it; and every ordinary program the workflows need is accepted.
/// </summary>
public class GrammarTests
{
    [Theory]
    [InlineData("for (let i = 0; i < 3; i++) {}", "G001")]
    [InlineData("for (const x of input.items) {}", "G001")]
    [InlineData("for (const k in input) {}", "G001")]
    [InlineData("while (true) {}", "G001")]
    [InlineData("do {} while (false)", "G001")]
    [InlineData("function f() { return 1; } f()", "G002")]
    [InlineData("[1].map(function (x) { return x; })", "G002")]
    [InlineData("(function () { return arguments.callee; })()", "G002")]
    [InlineData("class A {} new A()", "G003")]
    [InlineData("(class {})", "G003")]
    [InlineData("({ __proto__: input })", "G021")]
    [InlineData("({ '__proto__': input })", "G021")]
    [InlineData("({ get x() { return 1; } })", "G020")]
    [InlineData("({ set x(v) {} })", "G020")]
    [InlineData("({ f() { return 1; } })", "G020")]
    [InlineData("var v = 1; v", "G010")]
    [InlineData("this", "G030")]
    [InlineData("(async () => 1)()", "G031")]
    [InlineData("import('fs')", "G032")]
    [InlineData("new Array(5)", "G040")]
    [InlineData("new Date()", "G040")]
    [InlineData("new Proxy({}, {})", "G040")]
    [InlineData("new RegExp('x')", "G040")]
    [InlineData("new Function('return 1')", "G040")]
    [InlineData("Array(5)", "G041")]
    [InlineData("Object(1)", "G041")]
    [InlineData("eval('1')", "G041")]
    [InlineData("Function('return 1')", "G041")]
    [InlineData("try { 1 } catch (e) { 2 }", "G099")]
    [InlineData("switch (1) { default: }", "G099")]
    [InlineData("throw 1", "G099")]
    [InlineData("x: 1", "G099")]
    [InlineData("delete input.count", "G050")]
    [InlineData("void 0", "G050")]
    [InlineData("input instanceof Object", "G051")]
    [InlineData("1 | 2", "G051")]
    [InlineData("let a = 1; a |= 2; a", "G052")]
    [InlineData("(1, 2)", "G035")]
    [InlineData("[1, , 3]", "G012")]
    public void Constructs_outside_the_language_are_refused_at_parse(string source, string code)
    {
        var rejected = Attack.Run(source).Threw<GrammarRejectedException>();
        Assert.Contains(rejected.Violations, v => v.Code == code);
    }

    // Strict mode refuses these in the parser itself, before the grammar walk — earlier still.
    [Theory]
    [InlineData("with (input) { count }")]
    [InlineData("return 1")]
    [InlineData("if (true) { return 1 }")]
    [InlineData("const a = 1; a = 2; delete a")]
    [InlineData("implements = 1")]
    public void Strict_mode_syntax_errors_never_reach_the_grammar(string source) =>
        Attack.Run(source).Threw<Acornima.SyntaxErrorException>();

    /// <summary>
    /// sebastienros/jint#4076: a property miss on a 20,000-link prototype chain recurses in
    /// `ObjectInstance.Get`, which `StackOverflowGuard` does not probe, and ends the process. Every
    /// way to build such a chain is outside the language — the literal and the class here, and
    /// `Object.create`/`setPrototypeOf` at run time (`NoRouteToCodeOrHostTests`) — so the route
    /// is closed before the engine is involved.
    /// </summary>
    [Theory]
    [InlineData("input.items.reduce(a => ({ __proto__: a }), {}).missing", "G021")]
    [InlineData("input.items.reduce(a => class extends a {}, class {}).missing", "G003")]
    public void The_route_to_jint_4076_is_closed(string source, string code)
    {
        var rejected = Attack.Run(source, Attack.ScopeWithItems(20_000)).Threw<GrammarRejectedException>();
        Assert.Contains(rejected.Violations, v => v.Code == code);
    }

    [Fact]
    public void A_violation_carries_its_position()
    {
        var rejected = Attack.Run("const a = 1;\nfor (;;) {}").Threw<GrammarRejectedException>();
        var violation = Assert.Single(rejected.Violations);
        Assert.Equal(2, violation.Line);
    }

    [Theory]
    [InlineData("const f = n => n < 3 ? f(n + 1) : n; f(0)")]              // recursion via const: allowed, capped at run time
    [InlineData("const { name } = input.user; name")]                        // destructuring
    [InlineData("const [first, ...rest] = input.items; rest")]
    [InlineData("input.items.map(({ length }) => length)")]                  // destructuring in params
    [InlineData("input.items.map((x, i = 0) => x + i)")]                     // default parameter
    [InlineData("input.items.filter((x, i, all) => all.indexOf(x) === i)")] // three-argument callbacks
    [InlineData("input.items.reduce((acc, x) => { const y = x * 2; return acc + y; }, 0)")]  // block body with return
    [InlineData("input.count > 0 ? 'some' : 'none'")]
    [InlineData("input.user?.name ?? 'anon'")]
    [InlineData("input['co' + 'unt']")]
    [InlineData("({ ...input.user, [input.key]: 1 })")]
    [InlineData("[...new Set(input.items)]")]
    [InlineData("new Map(Object.entries(input.user)).size")]
    [InlineData("`${input.count} item(s)`")]
    [InlineData("'name' in input.user")]
    [InlineData("typeof input.count === 'number'")]
    [InlineData("let s = 0; input.items.forEach(x => { s += x; }); s")]
    [InlineData("let n = 0; n++; n")]
    [InlineData("const ok = input.count > 0;\nif (ok) { 1 } else { 2 }")]
    [InlineData("/<(.+)>/.exec('Alice <a@b.c>')[1]")]
    [InlineData("-input.count")]
    [InlineData("!input.missing")]
    public void Ordinary_programs_are_accepted(string source) =>
        Attack.Run(source).Returned();
}
