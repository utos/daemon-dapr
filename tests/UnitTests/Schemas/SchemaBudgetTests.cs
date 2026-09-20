using Common.Workflow;
using Execution.Domain.Schemas;
using Execution.Infrastructure.Schemas;
using UnitTests.Support;

namespace UnitTests.Schemas;

/// <summary>
/// <c>UTOS-H107</c> — a schema evaluation that exceeds its budget is reported rather than
/// survived.
/// <para>
/// The case it exists for is catastrophic backtracking. A <c>pattern</c> can be a perfectly valid
/// regular expression — so <c>UTOS-H010</c> accepts it at load — and still take exponential time
/// on the right string. The schema is not attacker-controlled: it lives in a bundle an operator
/// loaded. The <em>value</em> frequently is, because a transition's input is derived from an HTTP
/// activity's output, which is a third party's response body.
/// </para>
/// </summary>
public class SchemaBudgetTests
{
    [Fact]
    public void A_pattern_that_backtracks_catastrophically_is_reported_as_a_budget_failure()
    {
        // The textbook shape, and one an author reaches for by accident writing a hand-rolled
        // email or identifier pattern: nested quantifiers over an overlapping alternation.
        var schema = Wf.Fields(
            ("type", "object"),
            ("properties", Wf.Fields(
                ("code", Wf.Fields(("type", "string"), ("pattern", "^(a+)+$"))))));

        // A run of 'a's that cannot match, so the engine must try every partition of them.
        var value = Wf.Fields(("code", new string('a', 40) + "!"));

        var error = Assert.Throws<SchemaBudgetExceededException>(() =>
            new JsonSchemaValidator().Validate(schema, value, SchemaBoundary.Input));

        // Reported as the spec's failure rather than escaping as a RegexMatchTimeoutException,
        // which the driver would flatten to the generic EXECUTION_FAILED.
        Assert.Contains("pattern", error.Message);
    }

    [Fact]
    public void An_ordinary_pattern_is_unaffected()
    {
        var schema = Wf.Fields(
            ("type", "object"),
            ("properties", Wf.Fields(
                ("code", Wf.Fields(("type", "string"), ("pattern", "^A-[0-9]+$"))))));

        var result = new JsonSchemaValidator()
            .Validate(schema, Wf.Fields(("code", "A-42")), SchemaBoundary.Input);

        Assert.True(result.IsValid);
    }
}
