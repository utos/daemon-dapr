using Common.Workflow;
using Execution.Domain.Templating;

namespace Execution.Domain.Interpreter;

/// <summary>
/// A timer's <c>duration</c>, evaluated: a whole-field value that must render to a duration string
/// in the unit shorthand (<c>api/docs/workflow-source-format.md</c> § Durations). Anything else is
/// <c>UTOS-E106</c> — a number included, because <c>8</c> says neither seconds nor hours.
/// <para>
/// Its own function so that the interpreter and the evaluation conformance corpus's
/// <c>duration</c> form run the same check, rather than the corpus testing a copy of it — the
/// reason <see cref="ForEachCollection"/> is one.
/// </para>
/// <para>
/// A literal was already checked at load (<c>UTOS-C201</c>–<c>C203</c>), so what actually reaches
/// the failure here is a template that rendered to something else. That is a run-time failure for
/// the same reason a templated URL that renders to nonsense is: the value did not exist until the
/// run produced it.
/// </para>
/// </summary>
public static class DurationTemplate
{
    public const string NotADurationCode = "UTOS-E106";

    /// <summary>
    /// Renders <paramref name="duration"/> and parses it. Returns the rendered text beside the
    /// parsed value: the text is what history records — so a replay reads the same deadline rather
    /// than re-rendering against a later clock — and the value is what the timer waits.
    /// </summary>
    public static (string Rendered, TimeSpan Value) Resolve(
        ITemplateEngine templateEngine,
        string duration,
        TemplateScope scope,
        IDurationParser durationParser,
        string? activityName = null)
    {
        var where = activityName is null ? string.Empty : $"Timer activity '{activityName}' ";

        // Rendered to a string, not taken typed. A whole-field template that evaluates to the
        // number 8 must be refused rather than read as eight of something, and rendering first is
        // what makes `{{ 8 }}` and `"8"` the same refusal.
        var rendered = templateEngine.RenderToString(duration, scope);

        if (!durationParser.TryParse(rendered, out var value, out var failure))
        {
            throw new TemplateException(
                NotADurationCode,
                failure == DurationFailure.NotPositive
                    ? $"{where}resolved to a duration that is not positive: '{rendered}'."
                    : $"{where}resolved to '{rendered}', which is not a duration — expected the "
                      + "unit shorthand, e.g. '90s', '8h' or '1h30m'.");
        }

        return (rendered, value);
    }
}
