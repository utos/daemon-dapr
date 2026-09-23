using Utos.Workflows.V1;

namespace Common.Workflow.Proto;

/// <summary>
/// <see cref="IDurationParser"/> answered by the spec's own parser.
/// <para>
/// It lives here because this is the project that may see <c>Utos.Workflow</c>, and the reasoning
/// is the content digest's: a rule defined by the spec is computed where the spec's library is,
/// and the result is passed inward. Reimplementing the grammar in the domain would be a second set
/// of edge cases, and the first workflow to find the difference would be one the CLI accepted and
/// this daemon refused.
/// </para>
/// </summary>
public sealed class SpecDurationParser : IDurationParser
{
    public bool TryParse(string text, out TimeSpan value, out DurationFailure failure)
    {
        var parsed = WorkflowDuration.TryParse(text, out value, out var reason);

        failure = reason switch
        {
            WorkflowDuration.Failure.None => DurationFailure.None,
            WorkflowDuration.Failure.NotPositive => DurationFailure.NotPositive,
            _ => DurationFailure.Syntax
        };

        return parsed;
    }
}
