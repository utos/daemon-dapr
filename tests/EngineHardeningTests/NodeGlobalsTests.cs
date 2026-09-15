using EngineHardeningTests.Support;
using Execution.Domain.Templating;
using Execution.Infrastructure.Templating;

namespace EngineHardeningTests;

/// <summary>
/// § Node.js globals of the spec, from the hardening side: the values are deterministic per
/// evaluation, the real clock and PRNG are unreachable behind them, a Buffer is read-only and
/// cannot leave, and the host objects fail as JavaScript errors rather than as daemon
/// exceptions. The behaviours themselves — encodings, digests, the derivations — are the
/// evaluation corpus's to pin (<c>UnitTests/Templating/EvaluationConformanceTests</c>).
/// </summary>
public class NodeGlobalsTests
{
    private static readonly EvaluationMoment Moment =
        new(DateTimeOffset.Parse("2026-09-15T10:00:00Z"), Guid.Parse("6f2c8f5e-3b1a-4c7d-9e0f-1a2b3c4d5e6f"));

    private static object? Evaluate(string source, EvaluationMoment? moment = null)
    {
        var engine = new JintTemplateEngine(new Execution.Application.Models.TemplateEngineOptions());
        var scope = Attack.ScopeFrom(Attack.DefaultScope).WithMoment(moment ?? Moment);
        return engine.RenderTyped("{{ " + source + " }}", scope);
    }

    [Fact]
    public void Time_is_the_captured_instant_and_never_the_clock()
    {
        // Every spelling of "now" agrees with the moment, and with each other; the wall clock is
        // years away from it.
        Assert.Equal("2026-09-15T10:00:00.000Z", Evaluate("new Date().toISOString()"));
        Assert.Equal(1789466400000d, Convert.ToDouble(Evaluate("Date.now()")));
        Assert.Equal(true, Evaluate("Date.now() === new Date().getTime() && Date() === new Date().toString()"));

        // The real constructor is not reachable through an instance.
        Assert.Equal(true, Evaluate("new Date().constructor === Date && new Date(5).constructor === Date"));
        Assert.Equal("2026-09-15T10:00:00.000Z", Evaluate("new (new Date().constructor)().toISOString()"));
    }

    [Fact]
    public void Randomness_is_a_sequence_derived_from_the_seed()
    {
        // Same moment, two engines, same values; a different seed, different values. And the
        // sequence advances across calls rather than repeating.
        var first = Evaluate("[Math.random(), Math.random(), crypto.randomUUID(), crypto.randomUUID()]");
        var second = Evaluate("[Math.random(), Math.random(), crypto.randomUUID(), crypto.randomUUID()]");
        var other = Evaluate("[Math.random(), Math.random(), crypto.randomUUID(), crypto.randomUUID()]",
            Moment with { Seed = Guid.Parse("00000000-0000-4000-8000-000000000001") });

        Assert.Equal(Attack.Json(first), Attack.Json(second));
        Assert.NotEqual(Attack.Json(first), Attack.Json(other));

        var values = (List<object?>)first!;
        Assert.NotEqual(values[0], values[1]);
        Assert.NotEqual(values[2], values[3]);
        Assert.All(values.Take(2), v => Assert.InRange(Convert.ToDouble(v), 0, 1));
    }

    [Fact]
    public void An_unset_moment_is_the_epoch_and_the_nil_seed_not_the_clock()
    {
        // A scope nobody gave a moment evaluates at a fixed point, so nothing in the daemon can
        // fall back to wall-clock time by omission.
        var engine = new JintTemplateEngine(new Execution.Application.Models.TemplateEngineOptions());
        var scope = Attack.ScopeFrom(Attack.DefaultScope);

        Assert.Equal("1970-01-01T00:00:00.000Z", engine.RenderTyped("{{ new Date().toISOString() }}", scope));
    }

    [Theory]
    [InlineData("const b = Buffer.from('hi'); b[0] = 1; b[0]")]
    [InlineData("const b = Buffer.from('hi'); b.length = 0; b.length")]
    [InlineData("const b = Buffer.from('hi'); b.extra = 1; b.extra")]
    public void A_buffer_is_read_only(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Theory]
    [InlineData("Buffer.from('hi')")]
    [InlineData("({ raw: Buffer.from('hi') })")]
    [InlineData("new Date()")]
    [InlineData("new URL('https://x.test')")]
    [InlineData("new URLSearchParams('a=1')")]
    [InlineData("crypto.createHash('sha256')")]
    public void A_host_object_cannot_leave_the_engine(string source) =>
        Attack.Run(source).Threw("UTOS-E103");

    [Fact]
    public void The_refusal_says_what_to_return_instead()
    {
        Assert.Contains("toString('base64')", Attack.Run("Buffer.from('hi')").Threw("UTOS-E103").Message);
        Assert.Contains("toISOString", Attack.Run("new Date()").Threw("UTOS-E103").Message);
        Assert.Contains("href", Attack.Run("new URL('https://x.test')").Threw("UTOS-E103").Message);
    }

    [Theory]
    [InlineData("Buffer.from('hi', 'nope')")]
    [InlineData("Buffer.from(42)")]
    [InlineData("Buffer.from('hi').readUInt32LE(0)")]
    [InlineData("Buffer.concat(['x'])")]
    [InlineData("crypto.createHash('sha3')")]
    [InlineData("crypto.createHmac('sha256', 5)")]
    [InlineData("crypto.timingSafeEqual('a', 'bb')")]
    [InlineData("crypto.createHash('sha256').digest('hex') + crypto.createHash('sha256').update('x').digest().digest('hex')")]
    [InlineData("new URL('not a url')")]
    [InlineData("new URL('https://x.test').protocol = 'http:'")]
    public void A_host_object_misuse_is_a_script_error_not_a_daemon_error(string source) =>
        Attack.Run(source).Threw("UTOS-E120");

    [Fact]
    public void A_large_body_in_scope_is_one_buffer_not_a_million_elements()
    {
        // 1 MB of bytes: within the array-size limit it would still be a million JsValues; as a
        // Buffer it is one host object, and reading it is O(1) per byte.
        var engine = new JintTemplateEngine(new Execution.Application.Models.TemplateEngineOptions());
        var body = new byte[1 << 20];
        body[0] = 0x89; body[1] = (byte)'P'; body[2] = (byte)'N'; body[3] = (byte)'G';
        var scope = new TemplateScope()
            .With("response", new Dictionary<string, object?> { ["body"] = body, ["status"] = 200 })
            .With("input", new Dictionary<string, object?>());

        Assert.Equal(1 << 20, Convert.ToInt32(engine.RenderTyped("{{ response.body.length }}", scope)));
        Assert.Equal("PNG", engine.RenderTyped("{{ response.body.slice(1, 4).toString('latin1') }}", scope));
        Assert.Equal(true, engine.EvaluateCondition("response.body.readUInt8(0) === 0x89 && response.body.at(-1) === 0", scope));
    }

    [Fact]
    public void The_buffer_prototype_and_node_objects_are_frozen()
    {
        // The lockdown's freeze walk reaches what NodeGlobals installed: a program cannot patch a
        // method that every later expression of the activity would then run.
        Attack.Run("Buffer.from.x = 1; Buffer.from.x").Threw("UTOS-E120");
        Attack.Run("crypto.createHash = () => 1; crypto.createHash()").Threw("UTOS-E120");
        Attack.Run("Date.now = () => 1; Date.now()").Threw("UTOS-E120");
        Attack.Run("Buffer.from('a').constructor.from = null; Buffer.from('a')").Threw("UTOS-E120");
    }

    [Fact]
    public void Search_params_write_back_to_their_url()
    {
        Assert.Equal("https://x.test/p?a=1&b=2%263",
            Evaluate("const u = new URL('https://x.test/p?a=1'); u.searchParams.append('b', '2&3'); u.href"));
        Assert.Equal("https://x.test/p?b=2",
            Evaluate("const u = new URL('https://x.test/p?a=1'); u.searchParams.delete('a'); u.searchParams.set('b', '2'); u.toString()"));
    }
}
