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
    /// Check if a string contains template syntax
    /// </summary>
    bool HasTemplate(string? input);
}
