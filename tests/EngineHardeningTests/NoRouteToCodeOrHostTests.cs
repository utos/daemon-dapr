using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// § Surface, at run time: an allow-list of what exists, everything else deleted and the rest
/// frozen. `eval`, `Function()`, `new Date` and the like never get this far — the grammar refuses
/// them at load (`GrammarTests`). What is tested here is what the grammar cannot see: a name that
/// is syntactically fine but must not resolve to anything, and the `constructor` chains, where a
/// data object must have no route back to `Function` whether the key is written as a name,
/// assembled at run time, or read from the data itself.
/// </summary>
public class NoRouteToCodeOrHostTests
{
    [Theory]
    [InlineData("input.constructor.constructor('return 1')()")]
    [InlineData("input['constr' + 'uctor']['constructor']('return 1')()")]
    [InlineData("input[input.key][input.key]('return 1')()")]
    [InlineData("(() => {}).constructor('return 1')()")]
    [InlineData("utos.base64Decode.constructor('return 1')()")]
    [InlineData("Object.fromEntries([['toString', input.constructor.constructor]]) + ''")]
    public void A_constructor_chain_has_no_route_back_to_Function(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Theory]
    [InlineData("importNamespace('System').IO.File.ReadAllText('x')")]
    [InlineData("System.IO.File.ReadAllText('x')")]
    [InlineData("Reflect.ownKeys(input)")]
    [InlineData("Proxy.revocable({}, {})")]
    [InlineData("Date.now()")]
    [InlineData("Math.random()")]
    [InlineData("Object.create(null)")]
    [InlineData("Object.setPrototypeOf({}, null)")]
    [InlineData("Promise.resolve(1)")]
    [InlineData("setTimeout(() => 1, 0)")]
    [InlineData("'x'.repeat(2)")]
    [InlineData("'x'.padStart(4)")]
    [InlineData("Array.from({ length: 5 }, () => 0)")]
    public void Host_time_randomness_reflection_and_manufacturers_do_not_resolve(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Fact]
    public void The_removed_globals_are_undefined_rather_than_hidden() =>
        Assert.Equal(
            "undefined,undefined,undefined,undefined,undefined,undefined,undefined,undefined,undefined",
            Attack.Run("[typeof Function, typeof Proxy, typeof Reflect, typeof Date, typeof RegExp, typeof WeakRef, typeof Promise, typeof Math.random, typeof globalThis].join()")
                .Returned());

    [Theory]
    [InlineData("""JSON.parse('{"__proto__": {"polluted": 1}}').polluted""")]
    [InlineData("Object.fromEntries([['__proto__', { polluted: 1 }]]).polluted")]
    public void Prototype_pollution_through_data_does_not_reach_the_prototype(string source) =>
        Assert.Null(Attack.Run(source).Returned());

    [Fact]
    public void A_function_value_cannot_leave_the_engine() =>
        Assert.Contains("function", Attack.Run("input.items.map").Threw("UTOS-E103").Message);
}
