using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// Work that is small in statements and large in everything else, each stopped by a named
/// limit, quickly. Without loops or `Array.from`, every multiplication of work starts from the
/// input's own size; the array-size cap catches most of the shapes the sampled memory limit
/// would otherwise see too late.
/// </summary>
public class ResourceAttackTests
{
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(3);
    private static readonly string[] LimitCodes = ["UTOS-E110", "UTOS-E111", "UTOS-E112", "UTOS-E115"];

    [Theory]
    [InlineData("input.items.flatMap(a => input.items.flatMap(b => input.items.flatMap(c => input.items)))", 60)]      // 60⁴ elements
    [InlineData("input.items.map(a => input.items.map(b => input.big).join('')).join('')", 1000)]                     // 1 GB string
    [InlineData("input.items.reduce((acc, x) => acc.concat([x]), [])", 50_000)]                                        // quadratic
    [InlineData("`${Object.fromEntries([['length', 50000000], ['toString', [].join]])}`", 1)]                          // array-like join
    [InlineData("input.items.reduce(a => a.concat(a), [1]).length", 25)]                                               // array doubling
    [InlineData("input.items.flatMap(() => input.items).length", 1100)]                                                // 1.21M > MaxArraySize
    public void Allocation_attacks_are_stopped_by_a_named_limit(string source, int items)
    {
        var outcome = Attack.Run(source, Attack.ScopeWithItems(items, ", \"big\": \"" + new string('x', 1024) + "\""));
        var exception = outcome.ThrewAny();
        Assert.Contains(exception.Code, LimitCodes);
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Theory]
    [InlineData("/(a+)+$/.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab')")]
    [InlineData("'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab'.match(/(a+)+$/)")]
    [InlineData("'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab'.replace(/(a+)+$/, '')")]
    public void Catastrophic_regex_hits_the_regex_timeout_on_prepared_scripts_too(string source)
    {
        // The engine prepares scripts, which compiles regex literals at parse time with the
        // parsing options' timeout — a different setting from the engine constraint, and one that
        // defaults to ~10 s. Both are set; this is the one that would have been missed.
        var outcome = Attack.Run(source);
        outcome.Threw("UTOS-E117");
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }
}
