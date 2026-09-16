using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// § Surface, at run time: an allow-list of what exists, everything else deleted and the rest
/// frozen. `eval`, `Function()`, `new Proxy` and the like never get this far — the grammar refuses
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
    [InlineData("Buffer.from.constructor('return 1')()")]
    [InlineData("crypto.createHash.constructor('return 1')()")]
    [InlineData("Date.now.constructor('return 1')()")]
    [InlineData("new Date().constructor.constructor('return 1')()")]
    [InlineData("Object.fromEntries([['toString', input.constructor.constructor]]) + ''")]
    public void A_constructor_chain_has_no_route_back_to_Function(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Theory]
    [InlineData("importNamespace('System').IO.File.ReadAllText('x')")]
    [InlineData("System.IO.File.ReadAllText('x')")]
    [InlineData("Reflect.ownKeys(input)")]
    [InlineData("Proxy.revocable({}, {})")]
    [InlineData("Object.create(null)")]
    [InlineData("Object.setPrototypeOf({}, null)")]
    [InlineData("Promise.resolve(1)")]
    [InlineData("setTimeout(() => 1, 0)")]
    [InlineData("'x'.repeat(2)")]
    [InlineData("'x'.padStart(4)")]
    [InlineData("Array.from({ length: 5 }, () => 0)")]
    [InlineData("process.env.SECRET")]
    [InlineData("require('fs')")]
    [InlineData("fetch('https://x.test')")]
    [InlineData("utos.base64UrlDecode('aGk')")]
    public void Host_reflection_io_and_manufacturers_do_not_resolve(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Fact]
    public void The_removed_globals_are_undefined_rather_than_hidden() =>
        Assert.Equal(
            "undefined,undefined,undefined,undefined,undefined,undefined,undefined,undefined,undefined",
            Attack.Run("[typeof Function, typeof Proxy, typeof Reflect, typeof RegExp, typeof WeakRef, typeof Promise, typeof globalThis, typeof process, typeof utos].join()")
                .Returned());

    /// <summary>
    /// Spec 0.0.16 replaced time and randomness rather than removing them: the names resolve,
    /// to values derived from the evaluation's captured instant and seed. What must still not
    /// exist is the real clock and the real PRNG behind them — `NodeGlobalsTests` pins that.
    /// </summary>
    [Fact]
    public void The_node_globals_resolve() =>
        Assert.Equal(
            "function,function,object,object,function,function",
            Attack.Run("[typeof Date, typeof Math.random, typeof Buffer, typeof crypto, typeof URL, typeof URLSearchParams].join()")
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
