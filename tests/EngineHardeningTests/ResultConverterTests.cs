using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// § Values, the way back. A result is plain data or it is refused: a cycle, a function, a Map
/// or Set (which would silently become `{}`), a non-finite number. Numbers narrow by the daemon's
/// own rule; an integer beyond 2⁵³ is refused on the way in rather than rounded.
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
    public void Anything_but_plain_data_is_refused(string source, string reason) =>
        Assert.Contains(reason, Attack.Run(source).Threw("UTOS-E103").Message);

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("0 / 0")]
    public void A_non_finite_number_is_an_error(string source) => Attack.Run(source).Threw("UTOS-E102");

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
        var scope = """{"input": {"id": 9007199254740993}, "env": {}, "output": {}}""";
        Assert.Contains("2^53", Attack.Run("input.id", scope).Threw("UTOS-E104").Message);

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
    public void Undefined_and_null_both_come_back_as_null()
    {
        Assert.Null(Attack.Run("input.missing").Returned());
        Assert.Null(Attack.Run("null").Returned());
        Assert.Equal("""{"a":null}""", Attack.Json(Attack.Run("({ a: null })").Returned()));
    }

    [Fact]
    public void String_plus_number_concatenates_the_trap_the_spec_names() =>
        Assert.Equal("123451", Attack.Run("'12345' + 1").Returned());

    [Theory]
    [InlineData("n={{ input.count }}", "n=1")]
    [InlineData("{{ 5.0 }}", "5")]
    [InlineData("{{ 2.5 }}", "2.5")]
    [InlineData("{{ true }}/{{ null }}/{{ undefined }}", "true//")]
    [InlineData("{{ input.user }}", """{"name":"x"}""")]
    [InlineData("{{ input.items }}", "[3,1,2]")]
    public void Text_rendering_follows_the_spec(string template, string expected) =>
        Assert.Equal(expected, Attack.RunText(template).Returned());

    [Fact]
    public void A_condition_must_be_boolean()
    {
        Assert.Equal(true, Attack.RunCondition("input.count === 1").Returned());
        Assert.Equal(false, Attack.RunCondition("input.missing?.deep === 'x'").Returned());
        Attack.RunCondition("input.items").Threw("UTOS-E101");
        Attack.RunCondition("input.count").Threw("UTOS-E101");
        Attack.RunCondition("{{ input.count === 1 }}").Threw("UTOS-E061");
    }
}
