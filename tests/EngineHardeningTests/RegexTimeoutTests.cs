using System.Text.RegularExpressions;
using EngineHardeningTests.Support;

namespace EngineHardeningTests;

/// <summary>
/// §6.1. Two regex timeouts, and both must be set. `Engine.Evaluate(string)` honours the engine
/// constraint; a prepared script compiles its regex literals at parse time with the *parsing*
/// options' timeout (default ~10 s) and ignores the constraint. Measured: 197 ms versus 9,991 ms
/// for the same expression, and 109 ms once the parsing options carry the timeout too.
/// </summary>
public class RegexTimeoutTests
{
    private const string Catastrophic = "/(a+)+$/.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaab')";

    [Fact]
    public void Unprepared_evaluation_honours_the_engine_constraint()
    {
        var outcome = Attack.Run(Catastrophic, evaluator: new ReferenceEvaluator { PrepareScripts = false });
        outcome.Threw<RegexMatchTimeoutException>();
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(2), $"took {outcome.Elapsed}");
    }

    [Fact]
    public void Prepared_evaluation_honours_the_parsing_options_timeout()
    {
        var outcome = Attack.Run(Catastrophic, evaluator: new ReferenceEvaluator { PrepareScripts = true });
        outcome.Threw<RegexMatchTimeoutException>();
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(2), $"took {outcome.Elapsed}");
    }
}
