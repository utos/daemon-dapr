using Common.Workflow;
using Execution.Domain.Templating;

namespace Execution.Domain.Interpreter;

/// <summary>
/// Renders the template expressions inside a <see cref="ValueMap"/> — the shape every authored
/// transform uses — into plain CLR values.
/// </summary>
public sealed class StructRenderer(ITemplateEngine templateEngine)
{
    public Dictionary<string, object> Render(ValueMap source, TemplateScope scope)
    {
        var result = new Dictionary<string, object>();

        foreach (var (key, value) in source.Fields)
        {
            var rendered = RenderValue(value, scope);
            if (rendered != null)
                result[key] = rendered;
        }

        return result;
    }

    private object? RenderValue(object? value, TemplateScope scope)
    {
        return value switch
        {
            // Only strings carry templates; everything else is already a literal.
            string template => templateEngine.RenderTyped(template, scope),
            ValueMap nested => Render(nested, scope),
            IReadOnlyDictionary<string, object?> nested => Render(new ValueMap(nested), scope),
            IEnumerable<object?> items => items.Select(item => RenderValue(item, scope)).ToList(),
            _ => value
        };
    }
}
