namespace Execution.Domain.Templating;

/// <summary>
/// A template failed to parse, evaluate, or convert to the type the caller needed.
/// </summary>
public sealed class TemplateException : Exception
{
    public TemplateException(string message) : base(message)
    {
    }

    public TemplateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
