namespace Execution.Domain.Templating;

/// <summary>
/// Renders the template expressions a workflow author writes into activity configs and
/// transition transforms. The port lives in the domain because rendering is part of the
/// interpretation decision, not an infrastructure detail; the engine behind it is not.
/// </summary>
public interface ITemplateEngine
{
    /// <summary>
    /// Render a template to a string
    /// </summary>
    string RenderToString(string template, TemplateScope scope);

    /// <summary>
    /// Evaluate a template and return the result as the specified type
    /// </summary>
    T Evaluate<T>(string template, TemplateScope scope);

    /// <summary>
    /// Render a data value, keeping its native type when the whole string is a single
    /// expression.
    ///
    /// "{{ input.count + 1 }}" yields a number, so arithmetic and comparisons work on the
    /// next hop; "user-{{ input.id }}" is interpolation and yields a string. Rendering
    /// everything to a string would silently turn <c>+</c> into concatenation, which is
    /// what breaks counters threaded across a loop's back-edge.
    ///
    /// Only for structured data (transform values). URLs, headers and bodies are always
    /// strings and keep using <see cref="RenderToString"/>.
    /// </summary>
    object? RenderTyped(string template, TemplateScope scope);

    /// <summary>
    /// Check if a string contains template syntax
    /// </summary>
    bool HasTemplate(string? input);
}
