using System.Text.RegularExpressions;
using EngineHardeningTests.Support;
using Jint.Runtime;

namespace EngineHardeningTests;

/// <summary>
/// §6.5. Work that is small in statements and large in everything else, each stopped by a named
/// limit, quickly. Without loops or `Array.from`, every multiplication of work starts from the
/// input's own size; the array-size cap catches most of the shapes the sampled memory limit
/// would otherwise see too late.
/// </summary>
public class ResourceAttackTests
{
    private static readonly TimeSpan Quickly = TimeSpan.FromSeconds(3);

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
        var exception = outcome.Threw<Exception>();
        Assert.True(exception is MemoryLimitExceededException or StatementsCountOverflowException or TimeoutException,
            $"stopped by {exception.GetType().Name}: {exception.Message}");
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Theory]
    [InlineData("/(a+)+$/.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab')")]
    [InlineData("'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab'.match(/(a+)+$/)")]
    [InlineData("'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab'.replace(/(a+)+$/, '')")]
    public void Catastrophic_regex_hits_the_regex_timeout(string source)
    {
        var outcome = Attack.Run(source);
        outcome.Threw<RegexMatchTimeoutException>();
        Assert.True(outcome.Elapsed < Quickly, $"took {outcome.Elapsed}");
    }

    [Theory]
    [InlineData("'x'.repeat(200000000)")]
    [InlineData("'x'.padStart(200000000)")]
    [InlineData("'x'.padEnd(200000000)")]
    [InlineData("Array.from({ length: 2000000 }, () => 0)")]
    public void Manufacturers_of_size_from_a_number_do_not_exist(string source) =>
        Assert.Contains("is not a function", Attack.Run(source).Threw<JavaScriptException>().Message);
}
