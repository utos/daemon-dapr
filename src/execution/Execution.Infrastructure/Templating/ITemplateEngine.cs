using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.Templating;

public interface ITemplateEngine
{
    /// <summary>
    /// Render a template to a string
    /// </summary>
    string RenderToString(string template, TemplateContext context, ILogger logger);

    /// <summary>
    /// Evaluate a template and return the result as the specified type
    /// </summary>
    T Evaluate<T>(string template, TemplateContext context, ILogger logger);

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
    object? RenderTyped(string template, TemplateContext context, ILogger logger);

    /// <summary>
    /// Check if a string contains template syntax
    /// </summary>
    bool HasTemplate(string? input);
}
