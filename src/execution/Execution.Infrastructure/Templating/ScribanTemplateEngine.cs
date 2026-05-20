using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scriban;

namespace Execution.Infrastructure.Templating;

public sealed class ScribanTemplateEngine : ITemplateEngine
{
    // Regex to detect template syntax
    private static readonly Regex TemplateRegex = new(@"\{\{.*?\}\}|\{%.*?%\}", RegexOptions.Compiled);

    public string RenderToString(string template, TemplateContext context, ILogger logger)
    {
        if (string.IsNullOrEmpty(template) || !HasTemplate(template))
            return template;

        try
        {
            var scribanTemplate = Template.Parse(template);

            if (scribanTemplate.HasErrors)
            {
                var errors = string.Join(", ", scribanTemplate.Messages.Select(m => m.Message));
                logger.LogError("Template parsing failed: {Errors}", errors);
                throw new TemplateException($"Template parsing failed: {errors}");
            }

            var scriptContext = new Scriban.TemplateContext
            {
                EnableRelaxedMemberAccess = false,
                StrictVariables = true // Throw error on undefined variables
            };
            scriptContext.PushGlobal(context.GetScriptObject());

            var result = scribanTemplate.Render(scriptContext);
            return result;
        }
        catch (Exception ex) when (ex is not TemplateException)
        {
            logger.LogError(ex, "Template rendering failed for template: {Template}", template);
            throw new TemplateException($"Template rendering failed: {ex.Message}", ex);
        }
    }

    public T Evaluate<T>(string template, TemplateContext context, ILogger logger)
    {
        if (string.IsNullOrEmpty(template))
            return default!;

        if (!HasTemplate(template))
            // Try to convert the literal value to T
            return ConvertValue<T>(template);

        try
        {
            var scribanTemplate = Template.Parse(template);

            if (scribanTemplate.HasErrors)
            {
                var errors = string.Join(", ", scribanTemplate.Messages.Select(m => m.Message));
                logger.LogError("Template parsing failed: {Errors}", errors);
                throw new TemplateException($"Template parsing failed: {errors}");
            }

            var scriptContext = new Scriban.TemplateContext
            {
                EnableRelaxedMemberAccess = false,
                StrictVariables = true // Throw error on undefined variables
            };
            scriptContext.PushGlobal(context.GetScriptObject());

            var result = scribanTemplate.Evaluate(scriptContext);
            return ConvertValue<T>(result);
        }
        catch (Exception ex) when (ex is not TemplateException)
        {
            logger.LogError(ex, "Template evaluation failed for template: {Template}", template);
            throw new TemplateException($"Template evaluation failed: {ex.Message}", ex);
        }
    }

    public bool HasTemplate(string? input)
    {
        return !string.IsNullOrEmpty(input) && TemplateRegex.IsMatch(input);
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

public sealed class TemplateException : Exception
{
    public TemplateException(string message) : base(message)
    {
    }

    public TemplateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
