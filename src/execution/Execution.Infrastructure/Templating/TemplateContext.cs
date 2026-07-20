using Scriban.Runtime;

namespace Execution.Infrastructure.Templating;

public sealed class TemplateContext
{
    private readonly ScriptObject _scriptObject = new();

    public TemplateContext SetInput(object input)
    {
        _scriptObject["input"] = input;
        return this;
    }

    public TemplateContext SetOutput(object? output)
    {
        _scriptObject["output"] = output;
        return this;
    }

    /// <summary>
    /// Sets the run's ambient environment, available to every activity as {{ env.x }}.
    /// Always set it — an empty map makes {{ env.missing }} behave like any other missing
    /// key rather than failing on an undefined root object.
    /// </summary>
    public TemplateContext SetEnv(IDictionary<string, string>? env)
    {
        var scriptObject = new ScriptObject();

        if (env != null)
            foreach (var (key, value) in env)
                scriptObject[key] = value;

        _scriptObject["env"] = scriptObject;
        return this;
    }

    public TemplateContext SetWorkflow(object workflow)
    {
        _scriptObject["workflow"] = workflow;
        return this;
    }

    public TemplateContext Set(string key, object value)
    {
        _scriptObject[key] = value;
        return this;
    }

    internal ScriptObject GetScriptObject()
    {
        return _scriptObject;
    }
}
