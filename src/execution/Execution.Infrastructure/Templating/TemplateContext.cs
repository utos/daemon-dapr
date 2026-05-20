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
