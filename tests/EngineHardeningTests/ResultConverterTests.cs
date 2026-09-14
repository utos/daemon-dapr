using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// §6.3, the way back. A result is plain data or it is refused: a cycle, a function, a Map or Set
/// (which would silently become `{}`), a non-finite number. Getters, methods and class instances
/// cannot be written in the language at all (`GrammarTests`); the converter still refuses them,
/// as a second answer. Numbers narrow by the daemon's own rule.
/// </summary>
public class ResultConverterTests
{
    [Theory]
    [InlineData("const o = {}; o.self = o; o", "cycle")]
    [InlineData("const a = [1]; a.push(a); a", "cycle")]
    [InlineData("({ f: () => 1 })", "function")]
    [InlineData("({ toJSON: () => 1 })", "function")]
    [InlineData("new Map([['a', 1]])", "JsMap")]
    [InlineData("new Set([1])", "JsSet")]
    [InlineData("1 / 0", "non-finite")]
    [InlineData("0 / 0", "non-finite")]
    public void Anything_but_plain_data_is_refused(string source, string reason) =>
        Assert.Contains(reason, Attack.Run(source).Threw<ResultRejectedException>().Message);

    [Fact]
    public void A_symbol_keyed_function_is_dropped_never_invoked() =>
        Assert.Equal("""{"ok":1}""", Attack.Json(Attack.Run("({ [Symbol.toPrimitive]: () => { const f = () => f(); return f(); }, ok: 1 })").Returned()));

    [Theory]
    [InlineData("input.count + 1", typeof(int))]
    [InlineData("10 / 4", typeof(double))]
    [InlineData("3 / 4 * 100", typeof(int))]      // 75, exact — the Liquid case
    [InlineData("2 ** 40", typeof(long))]
    [InlineData("0.1 + 0.2", typeof(double))]
    public void Numbers_narrow_by_the_daemons_rule(string source, Type expected) =>
        Assert.IsType(expected, Attack.Run(source).Returned());

    [Fact]
    public void An_integer_beyond_2_53_is_refused_on_the_way_in_rather_than_rounded()
    {
        // 2^53 + 1 is the first integer a double cannot hold. Scriban carried a long exactly; a JS
        // number would round it silently — a numeric id would come back changed.
        var scope = """{"input": {"id": 9007199254740993}, "env": {}, "output": {}}""";
        Assert.Contains("2^53", Attack.Run("input.id", scope).Threw<ScopeRejectedException>().Message);

        var exact = """{"input": {"id": 9007199254740992}, "env": {}, "output": {}}""";
        Assert.Equal(9007199254740992L, Attack.Run("input.id", exact).Returned());
    }

    [Fact]
    public void A_whole_valued_double_input_divides_the_same_as_an_int()
    {
        // The reason for one number type: 20.0 from an API and 20 from a client must not differ.
        var scope = """{"input": {"a": 20.0, "b": 20, "q": 3}, "env": {}, "output": {}}""";
        Assert.Equal(Attack.Run("input.a / input.q", scope).Returned(), Attack.Run("input.b / input.q", scope).Returned());
    }

    [Fact]
    public void Undefined_becomes_null_and_null_stays_null()
    {
        Assert.Null(Attack.Run("input.missing").Returned());
        Assert.Null(Attack.Run("null").Returned());
        Assert.Equal("""{"a":null}""", Attack.Json(Attack.Run("({ a: null })").Returned()));
    }

    [Fact]
    public void String_plus_number_concatenates_the_trap_the_spec_names() =>
        Assert.Equal("123451", Attack.Run("'12345' + 1").Returned());
}
