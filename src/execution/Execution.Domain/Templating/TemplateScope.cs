namespace Execution.Domain.Templating;

/// <summary>
/// The variables a template is rendered against. Engine-agnostic on purpose: the domain
/// decides what is in scope, the adapter decides how to present it to its expression engine.
///
/// Every <c>With…</c> returns a new scope rather than mutating, so a base scope can be
/// derived from repeatedly — which is what <c>for_each</c> branch expansion does when it
/// binds a different item to the same alias per iteration.
/// </summary>
public sealed class TemplateScope
{
    private readonly Dictionary<string, object?> _values;

    public TemplateScope()
    {
        _values = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private TemplateScope(Dictionary<string, object?> values)
    {
        _values = values;
    }

    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>The input the current activity received, as <c>{{ input.x }}</c>.</summary>
    public TemplateScope WithInput(object? input)
    {
        return With("input", input);
    }

    /// <summary>The output the current activity produced, as <c>{{ output.x }}</c>.</summary>
    public TemplateScope WithOutput(object? output)
    {
        return With("output", output);
    }

    /// <summary>
    /// Sets the run's ambient environment, available to every activity as {{ env.x }}.
    /// Always set it — an empty map makes {{ env.missing }} behave like any other missing
    /// key rather than failing on an undefined root object.
    /// </summary>
    public TemplateScope WithEnv(IReadOnlyDictionary<string, string>? env)
    {
        return With("env", new TemplateNamespace(env ?? new Dictionary<string, string>()));
    }

    /// <summary>
    /// The HTTP response the current activity produced, as <c>{{ response.x }}</c>. Always set,
    /// even for an activity that made no request: see
    /// <see cref="Interpreter.HttpResponseContext.ToScopeValue"/> for why an absent response still
    /// has to carry its keys.
    /// </summary>
    public TemplateScope WithResponse(Interpreter.HttpResponseContext? response)
    {
        return With("response", Interpreter.HttpResponseContext.ToScopeValue(response));
    }

    /// <summary>
    /// Why the current activity failed, as <c>{{ error.x }}</c>. Always set, so that a condition
    /// mentioning it on a path where nothing failed evaluates false instead of throwing.
    /// </summary>
    public TemplateScope WithError(Interpreter.WorkflowErrorContext? error)
    {
        return With("error", Interpreter.WorkflowErrorContext.ToScopeValue(error));
    }

    public TemplateScope With(string key, object? value)
    {
        var copy = new Dictionary<string, object?>(_values, StringComparer.Ordinal)
        {
            [key] = value
        };

        return new TemplateScope(copy);
    }
}

/// <summary>
/// A named group of values addressed as a single root object in a template, such as
/// <c>env</c> in <c>{{ env.region }}</c>. Distinct from a plain dictionary value so an
/// adapter can present it as an object with members rather than as data.
/// </summary>
public sealed record TemplateNamespace(IReadOnlyDictionary<string, string> Values);
