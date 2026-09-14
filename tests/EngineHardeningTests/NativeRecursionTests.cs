using EngineHardeningTests.Support;
using Jint;
using Jint.Runtime;

namespace EngineHardeningTests;

/// <summary>
/// §6.1, the load-bearing option. `JSON.stringify` and `Array.prototype.flat` recurse in Jint's own
/// C#, where `LimitRecursion` (a JavaScript call counter) sees nothing. Without
/// `StackOverflowGuard` a 20,000-deep structure ends the process with exit 0xC00000FD — no catch,
/// no log, nothing. With it, a catchable RangeError in ~200 ms on the default 1 MB stack.
///
/// The structures are built by `reduce` over an input of that many elements: the language has no
/// loops, so depth can only come from data, and the input is what carries the cost.
///
/// The one native recursion the guard does not cover — a property miss walking a 20,000-link
/// prototype chain, sebastienros/jint#4076 — is unreachable from the language: `__proto__`
/// literals and classes are refused by the grammar, and `Object.create`/`setPrototypeOf` do not
/// exist at run time (`GrammarTests.The_route_to_jint_4076_is_closed`).
/// </summary>
public class NativeRecursionTests
{
    private const string Deep = "const deep = input.items.reduce(a => [a], []); ";
    private const string DeepObject = "const deep = input.items.reduce(a => ({ a }), {}); ";

    [Theory]
    [InlineData(Deep + "JSON.stringify(deep).length", 20_000)]
    [InlineData(Deep + "JSON.stringify(deep).length", 45_000)]
    [InlineData(DeepObject + "JSON.stringify(deep).length", 20_000)]
    [InlineData(Deep + "deep.flat(Infinity).length", 20_000)]
    [InlineData(Deep + "deep.flat(Infinity).length", 45_000)]
    [InlineData(Deep + "deep.flat(20000).length", 20_000)]
    public void Native_recursion_is_a_RangeError_not_a_dead_process(string source, int depth)
    {
        var outcome = Attack.Run(source, Attack.ScopeWithItems(depth));
        Assert.Contains("Maximum call stack size exceeded", outcome.Threw<JavaScriptException>().Message);
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(3), $"took {outcome.Elapsed}");
    }

    [Fact]
    public void StackOverflowGuard_is_set_on_every_engine()
    {
        // Read once at construction and off by default in 4.x. This pins that the evaluator turns
        // it on; the theory above pins that it is enough.
        var options = new Options();
        options.Constraints.StackOverflowGuard = new ReferenceEvaluator().StackOverflowGuard;
        Assert.True(options.Constraints.StackOverflowGuard);
    }

    [Theory]
    [InlineData(Deep + "deep", 20_000)]
    [InlineData(Deep + "deep", 45_000)]
    [InlineData(DeepObject + "deep", 20_000)]
    public void A_deep_result_is_refused_by_the_converter_before_it_recurses(string source, int depth) =>
        Assert.Contains("nested deeper than", Attack.Run(source, Attack.ScopeWithItems(depth)).Threw<ResultRejectedException>().Message);

    [Fact]
    public void JSON_parse_depth_is_capped()
    {
        var text = new string('[', 5000) + new string(']', 5000);
        var scope = Attack.ScopeWithItems(0, ", \"text\": \"" + text + "\"");
        Assert.Contains("depth", Attack.Run("JSON.parse(input.text).length", scope).Threw<JavaScriptException>().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JSON_parse_under_the_depth_cap_works()
    {
        var text = new string('[', 60) + new string(']', 60);
        var scope = Attack.ScopeWithItems(0, ", \"text\": \"" + text + "\"");
        Assert.Equal(1, Attack.Run("JSON.parse(input.text).length", scope).Returned());
    }

    [Theory]
    [InlineData(Deep + "String(deep).length")]
    [InlineData(Deep + "`${deep}`.length")]
    [InlineData(Deep + "deep == 1")]
    [InlineData(Deep + "[deep].includes(deep)")]
    [InlineData(Deep + "[deep, deep].sort().length")]
    [InlineData(Deep + "[deep].join(',').length")]
    [InlineData(DeepObject + "({...deep}).a.a.a === undefined")]
    public void Other_operations_on_deep_structures_complete_or_fail_cleanly(string source)
    {
        // Not asserting the value: the property is that the process is still here to assert.
        var outcome = Attack.Run(source, Attack.ScopeWithItems(20_000));
        Assert.False(outcome.HostMutated);
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(5), $"took {outcome.Elapsed}");
    }
}
