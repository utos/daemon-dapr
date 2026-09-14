using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// The other direction: the lockdown must leave what the workflows need. Each of these was once
/// broken by a hardening step that went too far (freezing globalThis, removing push, a guarded
/// property read) and is here so it stays fixed.
/// </summary>
public class LegitimateProgramsTests
{
    [Theory]
    [InlineData("const dbl = n => n * 2; dbl(input.count)", "2")]
    [InlineData("let v = input.count + 41; v", "42")]
    [InlineData("const sq = x => x * x; input.items.map(sq)", "[9,1,4]")]
    [InlineData("input['co' + 'unt']", "1")]
    [InlineData("const i = 1; input.items[i]", "1")]
    [InlineData("input.user.name['length']", "1")]
    [InlineData("[...new Set([1, 1, 2])]", "[1,2]")]
    [InlineData("new Set(input.items).size", "3")]
    [InlineData("Math.max(...input.items)", "3")]
    [InlineData("JSON.parse(JSON.stringify(input)).count", "1")]
    [InlineData("'Alice <a@b.c>'.match(/<(.+)>/)[1]", "\"a@b.c\"")]
    [InlineData("utos.base64UrlDecode('aGVsbG8')", "\"hello\"")]
    [InlineData("Object.hasOwn(input, 'count')", "true")]
    [InlineData("output.history.find(h => h.id === '1')?.id", "\"1\"")]
    [InlineData("output.history.find(h => h.id === '9')?.id ?? 'none'", "\"none\"")]
    [InlineData("input.missing?.deep === 'x'", "false")]
    [InlineData("encodeURIComponent('a b&c/é')", "\"a%20b%26c%2F%C3%A9\"")]
    [InlineData("const a = 1;\nconst b = a + 1;\nb * 2", "4")]
    [InlineData("const a = []; a.push(1); a.push(2); a", "[1,2]")]
    [InlineData("[3, 1, 2].sort((a, b) => a - b)", "[1,2,3]")]
    [InlineData("const o = { n: 0 }; o.n = 5; o.n", "5")]
    [InlineData("({ a: { b: 1 } }).a.b", "1")]                          // nested braces inside {{ }}
    [InlineData("'}}'", "\"}}\"")]                                        // the delimiter inside a string
    [InlineData("env.SECRET", "\"s3cret\"")]                              // the env namespace
    public void Ordinary_programs_work(string source, string expected) =>
        Assert.Equal(expected, Attack.Json(Attack.Run(source).Returned()));

    [Fact]
    public void Legitimately_deep_input_is_fine()
    {
        var node = "{\"leaf\": \"bottom\"}";
        for (var i = 0; i < 50; i++) node = "{\"a\": " + node + ", \"leaf\": \"L" + i + "\"}";
        var scope = "{\"input\": {\"deep\": " + node + "}, \"env\": {}, \"output\": {}}";

        Assert.Equal("L29", Attack.Run("input.deep" + string.Concat(Enumerable.Repeat(".a", 20)) + ".leaf", scope).Returned());
        Assert.Equal("L47", Attack.Run("({...input.deep}).a.a.leaf", scope).Returned());
    }

    [Fact]
    public void The_gmail_body_decode_in_one_program()
    {
        var body = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Hi there, ÿ")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var scope = "{\"input\": {}, \"env\": {}, \"output\": {\"payload\": {\"mimeType\": \"multipart/mixed\", \"parts\": ["
                    + "{\"mimeType\": \"multipart/alternative\", \"parts\": [{\"mimeType\": \"text/plain\", \"body\": {\"data\": \"" + body + "\"}}]},"
                    + "{\"mimeType\": \"application/pdf\", \"body\": {\"attachmentId\": \"x\"}}]}}}";

        var program = """
            const parts = output.payload.parts ?? [output.payload];
            const flat = parts.flatMap(p => [p, ...(p.parts ?? [])]);
            const data = flat.find(p => p.mimeType === 'text/plain' && p.body?.data !== undefined)?.body.data ?? '';
            utos.base64UrlDecode(data)
            """;

        Assert.Equal("Hi there, ÿ", Attack.Run(program, scope).Returned());
    }
}
