namespace Execution.Domain.Templating;

/// <summary>
/// Evaluates the expressions a workflow author writes — bare conditions, and the
/// <c>{{ }}</c> templates in activity configs and transition transforms — as
/// <c>api/docs/template-expressions.md</c> defines them. The port lives in the domain because
/// evaluation is part of the interpretation decision, not an infrastructure detail; the engine
/// behind it is not.
/// </summary>
public interface ITemplateEngine
{
    /// <summary>
    /// A condition: the whole string is one program, no delimiters, and its value must be
    /// exactly <c>true</c> or <c>false</c> — anything else is an error, never a branch taken.
    /// </summary>
    bool EvaluateCondition(string condition, TemplateScope scope);

    /// <summary>
    /// A data value, keeping its native type when the whole string is a single <c>{{ }}</c>
    /// program: <c>"{{ input.count + 1 }}"</c> yields a number, so arithmetic works on the next
    /// hop, and an object or array survives as one. Interpolation — <c>"user-{{ input.id }}"</c>
    /// — yields a string, and a string with no <c>{{</c> is a literal and comes back as itself.
    /// A whole-field program whose value is <c>undefined</c> yields null.
    ///
    /// Only for structured data (transform values). URLs, headers and bodies are always text and
    /// use <see cref="RenderToString"/>.
    /// </summary>
    object? RenderTyped(string template, TemplateScope scope);

    /// <summary>
    /// Text with its <c>{{ }}</c> segments rendered in place: a string as itself, a number in its
    /// shortest round-trip form, <c>null</c>/<c>undefined</c> as nothing, an object or array as
    /// JSON. A whole-field template is rendered too, never returned typed.
    /// </summary>
    string RenderToString(string template, TemplateScope scope);
}
