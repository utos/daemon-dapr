using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// § Runtime guarantees. Every limit fires, quickly, as its own code. The language has no
/// `try`/`catch` (`GrammarTests`), so there is nothing an expression could do about it even if
/// the exception were visible — and it is not: Jint raises these outside JavaScript entirely.
/// Without loops, every attack here gets its iteration count from the input it is handed.
/// </summary>
public class LimitTests
{
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(3);

    [Fact]
    public void Statement_budget_counts_callback_invocations()
    {
        // 100 + 100² + 100³ callbacks, well past the 100k budget.
        var outcome = Attack.Run(
            "input.items.map(() => input.items.map(() => input.items.map(() => 0))).length",
            Attack.ScopeWithItems(100));
        outcome.Threw("UTOS-E110");
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Fact]
    public void Memory_limit_fires_on_string_doubling()
    {
        // 40 doublings of 'x' is a terabyte; the limit is 64 MB.
        var outcome = Attack.Run("input.items.reduce(s => s + s, 'x').length", Attack.ScopeWithItems(40));
        outcome.Threw("UTOS-E111");
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Fact]
    public void Recursion_limit_fires_on_a_self_calling_helper() =>
        Attack.Run("const f = n => f(n + 1); f(0)").Threw("UTOS-E113");

    [Fact]
    public void Wall_clock_timeout_fires_when_the_statement_budget_does_not()
    {
        // A CPU-bound comparator: few statements, much time.
        var patient = Attack.Engine(o =>
        {
            o.MaxStatements = 0;
            o.TimeoutMilliseconds = 500;
            o.MemoryLimitBytes = 4L << 30;
        });
        var outcome = Attack.Run(
            "input.items.toSorted((a, b) => input.items.reduce((s, x) => s + x, 0) * 0 + (a - b))",
            Attack.ScopeWithItems(5000), patient);

        outcome.Threw("UTOS-E112");
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Fact]
    public void The_realistic_five_thousand_record_flatten_fits_the_default_budget()
    {
        // The budget must not fire in ordinary use. 5,000 history records → ids, deduplicated.
        var records = string.Join(",", Enumerable.Range(0, 5000).Select(i =>
            "{\"id\": \"" + i + "\", \"messagesAdded\": [{\"message\": {\"id\": \"m" + i % 4000 + "\"}}]}"));
        var scope = "{\"input\": {}, \"env\": {}, \"output\": {\"history\": [" + records + "]}}";

        var value = Attack.Run(
            "[...new Set(output.history.flatMap(h => h.messagesAdded ?? []).map(m => m.message.id))]", scope).Returned();

        Assert.Equal(4000, Assert.IsType<List<object?>>(value).Count);
    }
}
