using EngineHardeningTests.Support;
using Jint.Runtime;

namespace EngineHardeningTests;

/// <summary>
/// §6.5. Every limit fires, quickly, and surfaces as a host exception. The language has no
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
        outcome.Threw<StatementsCountOverflowException>();
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Fact]
    public void Memory_limit_fires_on_string_doubling()
    {
        // 40 doublings of 'x' is a terabyte; the limit is 64 MB.
        var outcome = Attack.Run("input.items.reduce(s => s + s, 'x').length", Attack.ScopeWithItems(40));
        outcome.Threw<MemoryLimitExceededException>();
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Fact]
    public void Recursion_limit_fires_on_a_self_calling_helper() =>
        Attack.Run("const f = n => f(n + 1); f(0)").Threw<RecursionDepthOverflowException>();

    [Fact]
    public void Wall_clock_timeout_fires_when_the_statement_budget_does_not()
    {
        // A CPU-bound comparator: few statements, much time.
        var patient = new ReferenceEvaluator
        {
            Limits = EvaluationLimits.Default with { MaxStatements = 0, Timeout = TimeSpan.FromMilliseconds(500), MemoryBytes = 4L << 30 },
        };
        var outcome = Attack.Run(
            "input.items.toSorted((a, b) => input.items.reduce((s, x) => s + x, 0) * 0 + (a - b))",
            Attack.ScopeWithItems(5000), patient);

        outcome.Threw<TimeoutException>();
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Fact]
    public void Cancellation_token_is_honoured()
    {
        var patient = new ReferenceEvaluator
        {
            Limits = EvaluationLimits.Default with { MaxStatements = 0, Timeout = TimeSpan.FromSeconds(60), MemoryBytes = 4L << 30 },
        };
        var outcome = Attack.Run(
            "input.items.toSorted((a, b) => input.items.reduce((s, x) => s + x, 0) * 0 + (a - b))",
            Attack.ScopeWithItems(5000), patient, cancelAfter: TimeSpan.FromMilliseconds(200));

        outcome.Threw<ExecutionCanceledException>();
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
