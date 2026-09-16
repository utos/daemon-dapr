using Google.Protobuf.WellKnownTypes;
using Utos.Workflows.V1;
using Utos.Workflows.V1.Validation;

namespace EngineHardeningTests;

/// <summary>
/// The grammar (§ Grammar of the spec) is the shared validator's, enforced when a bundle is
/// loaded — the same tree the engine then runs. These drive it on a minimal bundle: every
/// construct that manufactures iteration, reaches code, or builds a prototype chain is refused
/// before the engine sees it, and every ordinary program is accepted.
/// </summary>
public class GrammarTests
{
    [Theory]
    [InlineData("for (let i = 0; i < 3; i++) {} true", "UTOS-E001")]
    [InlineData("for (const x of input.items) {} true", "UTOS-E001")]
    [InlineData("while (true) {} true", "UTOS-E001")]
    [InlineData("const f = function () { return 1; }; f() === 1", "UTOS-E002")]
    [InlineData("(class {}) !== null", "UTOS-E003")]
    [InlineData("try { 1 } catch (e) { 2 } true", "UTOS-E004")]
    [InlineData("var v = 1; v > 0", "UTOS-E010")]
    [InlineData("({ __proto__: input }) !== null", "UTOS-E021")]
    [InlineData("({ get x() { return 1; } }).x === 1", "UTOS-E020")]
    [InlineData("this === undefined", "UTOS-E030")]
    [InlineData("(async () => 1)() !== null", "UTOS-E031")]
    [InlineData("new Array(5).length === 5", "UTOS-E040")]
    [InlineData("new Proxy({}, {}) !== null", "UTOS-E040")]
    [InlineData("Array(5).length === 5", "UTOS-E041")]
    [InlineData("eval('true')", "UTOS-E041")]
    [InlineData("Function('return true')()", "UTOS-E041")]
    [InlineData("delete input.count", "UTOS-E050")]
    [InlineData("void 0 === undefined", "UTOS-E050")]
    [InlineData("(input instanceof Object) === true", "UTOS-E051")]
    [InlineData("(1, true)", "UTOS-E035")]
    [InlineData("output.status ===", "UTOS-E060")]
    [InlineData("{{ output.status === 'ready' }}", "UTOS-E061")]
    public void Constructs_outside_the_language_are_refused_at_load(string condition, string code)
    {
        var report = WorkflowBundleValidator.Validate(BundleWithCondition(condition));
        Assert.Contains(report.Issues, issue => issue.Code == code);
    }

    /// <summary>
    /// sebastienros/jint#4076: a property miss on a 20,000-link prototype chain recurses in
    /// `ObjectInstance.Get`, which `StackOverflowGuard` does not probe, and ends the process. Every
    /// way to build such a chain is outside the language — the literal and the class here, and
    /// `Object.create`/`setPrototypeOf` at run time (`NoRouteToCodeOrHostTests`).
    /// </summary>
    [Theory]
    [InlineData("input.items.reduce(a => ({ __proto__: a }), {}).missing === undefined", "UTOS-E021")]
    [InlineData("input.items.reduce(a => class extends a {}, class {}).missing === undefined", "UTOS-E003")]
    public void The_route_to_jint_4076_is_closed(string condition, string code)
    {
        var report = WorkflowBundleValidator.Validate(BundleWithCondition(condition));
        Assert.Contains(report.Issues, issue => issue.Code == code);
    }

    [Theory]
    [InlineData("const f = n => n < 3 ? f(n + 1) : n; f(0) === 3")]
    [InlineData("input.items.map(({ length }) => length).length > 0")]
    [InlineData("input.items.reduce((acc, x) => { const y = x * 2; return acc + y; }, 0) > 0")]
    [InlineData("input.user?.name ?? 'anon'")]
    [InlineData("[...new Set(input.items)].length === 3")]
    [InlineData("let s = 0; input.items.forEach(x => { s += x; }); s === 6")]
    [InlineData("typeof input.count === 'number' && 'name' in input.user")]
    [InlineData("/<(.+)>/.exec('Alice <a@b.c>')[1] === 'a@b.c'")]
    // Spec 0.0.16: bitwise and shift operators, every assignment operator, and the Node globals'
    // constructors are in the language.
    [InlineData("let f = input.count; f |= 8; (f & 4) === 0 && (f >>> 1) > 0 && ~0 === -1")]
    [InlineData("new Date(0).getTime() === 0 && new URL('https://x.test/a').pathname === '/a'")]
    [InlineData("new URLSearchParams({ q: 'a b' }).toString() === 'q=a+b'")]
    [InlineData("Buffer.from('aGk', 'base64').toString() === 'hi'")]
    public void Ordinary_programs_are_accepted(string condition)
    {
        var report = WorkflowBundleValidator.Validate(BundleWithCondition(condition));
        Assert.True(report.IsValid, report.ToString());
    }

    private static WorkflowBundle BundleWithCondition(string condition)
    {
        var workflow = new Workflow
        {
            ApiVersion = WorkflowDocument.ApiVersion,
            Kind = WorkflowDocument.Kind,
            Metadata = new WorkflowMetadata { Name = "greet", Version = "1.0.0" },
            Spec = new WorkflowSpec(),
        };
        workflow.Spec.Activities["say-hello"] = new WorkflowActivity
        {
            Http = new HttpActivityConfig { Method = "GET", Url = "https://api.example.com/hello" },
            OnSuccess = { new TransitionRule { Condition = condition, Result = new Google.Protobuf.WellKnownTypes.Struct() } },
        };

        var bundle = new WorkflowBundle { EntryPoint = "greet:1.0.0" };
        bundle.Workflows["greet:1.0.0"] = workflow;
        return bundle;
    }
}
