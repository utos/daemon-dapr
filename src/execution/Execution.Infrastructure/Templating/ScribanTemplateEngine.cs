using System.Text.RegularExpressions;
using Execution.Domain.Templating;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace Execution.Infrastructure.Templating;

public sealed class ScribanTemplateEngine(ILogger<ScribanTemplateEngine> logger) : ITemplateEngine
{
    // Regex to detect template syntax
    private static readonly Regex TemplateRegex = new(@"\{\{.*?\}\}|\{%.*?%\}", RegexOptions.Compiled);

    public string RenderToString(string template, TemplateScope scope)
    {
        if (string.IsNullOrEmpty(template) || !HasTemplate(template))
            return template;

        try
        {
            var scribanTemplate = Parse(template);

            var result = scribanTemplate.Render(CreateContext(scope));
            return result;
        }
        catch (Exception ex) when (ex is not TemplateException)
        {
            logger.LogError(ex, "Template rendering failed for template: {Template}", template);
            throw new TemplateException($"Template rendering failed: {ex.Message}", ex);
        }
    }

    public T Evaluate<T>(string template, TemplateScope scope)
    {
        if (string.IsNullOrEmpty(template))
            return default!;

        if (!HasTemplate(template))
            // Try to convert the literal value to T
            return ConvertValue<T>(template);

        try
        {
            var scribanTemplate = Parse(template);

            var result = scribanTemplate.Evaluate(CreateContext(scope));
            return ConvertValue<T>(result);
        }
        catch (Exception ex) when (ex is not TemplateException)
        {
            logger.LogError(ex, "Template evaluation failed for template: {Template}", template);
            throw new TemplateException($"Template evaluation failed: {ex.Message}", ex);
        }
    }

    public object? RenderTyped(string template, TemplateScope scope)
    {
        if (string.IsNullOrEmpty(template) || !HasTemplate(template))
            return template;

        // Interpolation ("user-{{ input.id }}") only makes sense as text.
        if (!IsWholeExpression(template))
            return RenderToString(template, scope);

        return Normalize(Evaluate<object?>(template, scope));
    }

    public bool HasTemplate(string? input)
    {
        return !string.IsNullOrEmpty(input) && TemplateRegex.IsMatch(input);
    }

    private Template Parse(string template)
    {
        var scribanTemplate = Template.Parse(template);

        if (!scribanTemplate.HasErrors)
            return scribanTemplate;

        var errors = string.Join(", ", scribanTemplate.Messages.Select(m => m.Message));
        logger.LogError("Template parsing failed: {Errors}", errors);
        throw new TemplateException($"Template parsing failed: {errors}");
    }

    /// <summary>
    /// Projects the engine-agnostic scope onto Scriban's globals. A
    /// <see cref="TemplateNamespace"/> becomes a <see cref="ScriptObject"/> so that, under
    /// <c>StrictVariables</c>, a missing member of a present root ({{ env.missing }}) behaves
    /// like any other missing key rather than failing as an undefined variable.
    /// </summary>
    private static Scriban.TemplateContext CreateContext(TemplateScope scope)
    {
        var globals = new ScriptObject();

        foreach (var (key, value) in scope.Values)
            globals[key] = value is TemplateNamespace ns ? ToScriptObject(ns) : value;

        var context = new Scriban.TemplateContext
        {
            EnableRelaxedMemberAccess = false,
            StrictVariables = true // Throw error on undefined variables
        };
        context.PushGlobal(globals);

        return context;
    }

    private static ScriptObject ToScriptObject(TemplateNamespace ns)
    {
        var scriptObject = new ScriptObject();

        foreach (var (key, value) in ns.Values)
            scriptObject[key] = value;

        return scriptObject;
    }

    /// <summary>
    /// True when the string is exactly one template expression and nothing else, so its
    /// value can stand on its own rather than being spliced into surrounding text.
    /// </summary>
    private static bool IsWholeExpression(string template)
    {
        var trimmed = template.Trim();
        var matches = TemplateRegex.Matches(trimmed);

        return matches.Count == 1
               && matches[0].Index == 0
               && matches[0].Length == trimmed.Length;
    }

    /// <summary>
    /// Converts Scriban's runtime collections into plain CLR types, so values that leave the
    /// engine serialize predictably into execution state.
    /// </summary>
    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
            case string:
                return value;

            // ScriptObject implements IDictionary<string, object>.
            case IDictionary<string, object> dictionary:
                return dictionary.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value));

            // ScriptArray implements IList<object>. Strings are excluded above.
            case System.Collections.IEnumerable enumerable:
                return enumerable.Cast<object?>().Select(Normalize).ToList();

            default:
                return value;
        }
    }

    private static T ConvertValue<T>(object? value)
    {
        switch (value)
        {
            case T directValue:
                return directValue;
            case null:
                return default!;
            default:
                try
                {
                    // Handle common type conversions
                    if (typeof(T) == typeof(bool))
                        return (T)(object)Convert.ToBoolean(value);

                    if (typeof(T) == typeof(string))
                        return (T)(object)value.ToString()!;

                    if (typeof(T) == typeof(int))
                        return (T)(object)Convert.ToInt32(value);

                    if (typeof(T) == typeof(double))
                        return (T)(object)Convert.ToDouble(value);

                    // Try general conversion
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch (Exception ex)
                {
                    throw new TemplateException(
                        $"Cannot convert value '{value}' to type {typeof(T).Name}: {ex.Message}", ex);
                }
        }
    }
}
