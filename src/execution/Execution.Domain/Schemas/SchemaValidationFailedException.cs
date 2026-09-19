using Common.Workflow;

namespace Execution.Domain.Schemas;

/// <summary>
/// A value did not satisfy the declaration applied to it — one of <c>UTOS-H101</c>–<c>H106</c>.
/// <para>
/// Which boundary it came from decides what happens next, and the spec is deliberate about it:
/// <c>H101</c>/<c>H102</c> are raised before a run exists and become <c>INVALID_ARGUMENT</c>;
/// <c>H104</c> is the invoking construct's failure and its <c>onFailure</c> may catch it;
/// <c>H103</c>, <c>H105</c> and <c>H106</c> fail the run and are <em>not</em> catchable. An
/// output failure is not a policy an author opts into — a workflow returning the wrong shape is
/// broken, and the caller that trusted its contract would otherwise carry the damage.
/// </para>
/// </summary>
public sealed class SchemaValidationFailedException(
    string code,
    SchemaBoundary boundary,
    SchemaValidationResult result)
    : Exception(Describe(code, boundary, result))
{
    /// <summary>The rule code, from <see cref="SchemaValidation"/>.</summary>
    public string Code { get; } = code;

    public SchemaBoundary Boundary { get; } = boundary;

    public SchemaValidationResult Result { get; } = result;

    /// <summary>
    /// The failure's <c>details</c>, shaped as <c>api/docs/workflow-schemas.md</c> specifies:
    /// a <c>schema</c> key carrying the boundary, the complete failure list and the truncated
    /// count.
    /// <para>
    /// The <c>schema</c> key is what puts this in scope for an <c>onFailure</c> rule as
    /// <c>error.details.schema</c>, so a caller can branch on <em>which field</em> was wrong
    /// rather than on message text.
    /// </para>
    /// </summary>
    public ValueMap ToDetails() =>
        ValueMap.From([
            new KeyValuePair<string, object?>("schema", ValueMap.From([
                new KeyValuePair<string, object?>("boundary", BoundaryName(Boundary)),
                new KeyValuePair<string, object?>("errors", Result.Failures
                    .Select(object? (failure) => ValueMap.From([
                        new KeyValuePair<string, object?>("instanceLocation", failure.InstanceLocation),
                        new KeyValuePair<string, object?>("keyword", failure.Keyword),
                        new KeyValuePair<string, object?>("keywordLocation", failure.KeywordLocation)
                    ]))
                    .ToList()),
                new KeyValuePair<string, object?>("truncated", Result.Truncated)
            ]))
        ]);

    /// <summary>
    /// Lowercase, matching the spec's <c>boundary</c> values and the corpus's case field. It
    /// reaches an author, so it is not the CLR enum's spelling by accident.
    /// </summary>
    public static string BoundaryName(SchemaBoundary boundary) => boundary switch
    {
        SchemaBoundary.Input => "input",
        SchemaBoundary.Env => "env",
        SchemaBoundary.Output => "output",
        SchemaBoundary.Emits => "emits",
        _ => boundary.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Message text is not contract — the code and the failure list are — but it is what an
    /// operator reads first, so it names the locations rather than restating the code.
    /// </summary>
    private static string Describe(string code, SchemaBoundary boundary, SchemaValidationResult result)
    {
        var locations = result.Failures
            .Take(5)
            .Select(f => (f.InstanceLocation.Length == 0 ? "(root)" : f.InstanceLocation) + " (" + f.Keyword + ")");

        var summary = string.Join(", ", locations);
        if (result.Failures.Count > 5) summary += ", …";

        var total = result.Failures.Count + result.Truncated;

        return $"{code}: the {BoundaryName(boundary)} value does not satisfy the declared schema — "
               + $"{total} failure{(total == 1 ? "" : "s")}: {summary}";
    }
}
