namespace Execution.Domain.Templating;

/// <summary>
/// An expression could not be evaluated. <see cref="Code"/> is the spec's evaluation code
/// (<c>UTOS-E1##</c>, <c>api/docs/template-expressions.md</c>) — a condition that was not a
/// boolean, a limit that fired, a result that was not plain data, a script error — and is the
/// contract; the message is not.
/// </summary>
public sealed class TemplateException : Exception
{
    public TemplateException(string code, string message) : base(message)
    {
        Code = code;
    }

    public TemplateException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>The stable <c>UTOS-E1##</c> code naming what went wrong.</summary>
    public string Code { get; }

    public override string ToString() => Code + ": " + Message;
}
