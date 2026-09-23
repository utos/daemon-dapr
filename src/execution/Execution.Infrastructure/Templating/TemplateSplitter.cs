using Acornima;
using Acornima.Ast;
using Execution.Domain.Templating;

namespace Execution.Infrastructure.Templating;

/// <summary>One piece of a templated string: literal text, or the source of a <c>{{ }}</c> program.</summary>
public sealed record TemplateSegment(bool IsExpression, string Text);

/// <summary>
/// The parser every expression in this engine is read with, in one place so the two parse sites —
/// finding a program's end while splitting, and wrapping it to run — cannot disagree about what
/// the language is.
/// <para>
/// <c>AllowAwaitOutsideFunction</c> is what <c>api/docs/template-expressions.md</c> § Awaiting
/// means by "await is permitted at a program's top level": <c>bytes()</c> and <c>text()</c> are
/// asynchronous and there is no enclosing function to put the <c>await</c> inside.
/// </para>
/// <para>
/// The shared validator has admitted <c>await</c> since spec 0.20 while this parser still refused
/// it, so a document passed load-time validation and then failed at run time with
/// <c>UTOS-E060</c> — a parse error, on an expression the spec says parses. Accept-then-fail is the
/// worst shape a rule can have, and the evaluation conformance corpus is what named it.
/// </para>
/// </summary>
internal static class ExpressionParser
{
    internal static Parser Create() => new(new ParserOptions { AllowAwaitOutsideFunction = true });
}

/// <summary>
/// Splits a templated string the way <c>api/docs/template-expressions.md</c> § Forms defines
/// it: at <c>{{</c>, one complete program is parsed and must be followed by <c>}}</c>. The
/// closing delimiter is found by parsing, never by scanning, which is what lets
/// <c>{{ {a: {b: 1}} }}</c> be one program containing an object literal. The same algorithm
/// the shared validator applies, so a field that validated splits identically here.
/// </summary>
public static class TemplateSplitter
{
    private const string Open = "{{";
    private const string Close = "}}";

    public static IReadOnlyList<TemplateSegment> Split(string text)
    {
        var segments = new List<TemplateSegment>();
        var position = 0;

        while (true)
        {
            var open = text.IndexOf(Open, position, StringComparison.Ordinal);
            if (open < 0)
            {
                if (position < text.Length) segments.Add(new TemplateSegment(false, text[position..]));
                return segments;
            }

            if (open > position) segments.Add(new TemplateSegment(false, text[position..open]));

            var contentStart = open + Open.Length;
            var close = text.IndexOf(Close, contentStart, StringComparison.Ordinal);
            if (close < 0)
                throw new TemplateException(EvaluationCodes.Unclosed,
                    $"'{{{{' at offset {open} is never closed by '}}}}'.");

            string? lastError = null;
            var parsedEnd = -1;
            while (close >= 0)
            {
                var candidate = text[contentStart..close];
                try
                {
                    ExpressionParser.Create().ParseScript(candidate, strict: true);
                    parsedEnd = close;
                    break;
                }
                catch (SyntaxErrorException error)
                {
                    lastError = error.Message;
                }

                close = text.IndexOf(Close, close + 1, StringComparison.Ordinal);
            }

            if (parsedEnd < 0)
                throw new TemplateException(EvaluationCodes.SyntaxError,
                    $"The expression starting at offset {open} does not parse: {lastError}");

            segments.Add(new TemplateSegment(true, text[contentStart..parsedEnd]));
            position = parsedEnd + Close.Length;
        }
    }

    /// <summary>One program with nothing but whitespace around it.</summary>
    public static bool IsWholeField(IReadOnlyList<TemplateSegment> segments)
    {
        var expressions = 0;
        foreach (var segment in segments)
        {
            if (segment.IsExpression) expressions++;
            else if (segment.Text.Trim().Length > 0) return false;
        }

        return expressions == 1;
    }

    public static string WholeFieldProgram(IReadOnlyList<TemplateSegment> segments) =>
        segments.First(segment => segment.IsExpression).Text;
}

/// <summary>
/// One function scope per program: the source is wrapped in an arrow IIFE so its declarations
/// are locals of that call, and its completion value — the final expression statement — becomes
/// the return value. An empty program, or one ending in a declaration, has no value.
/// </summary>
public static class ProgramWrapper
{
    /// <returns>
    /// The wrapped source, and whether it was made <c>async</c> — which the caller needs, because
    /// only a promise this wrapper introduced may be unwrapped.
    /// </returns>
    public static (string Source, bool Awaits) Wrap(string source)
    {
        Program program;
        try
        {
            program = ExpressionParser.Create().ParseScript(source, strict: true);
        }
        catch (SyntaxErrorException error)
        {
            throw new TemplateException(EvaluationCodes.SyntaxError, "The expression does not parse: " + error.Message, error);
        }

        var body = program.Body;
        if (body.Count == 0 || body[^1] is not ExpressionStatement last)
            throw new TemplateException(EvaluationCodes.NoValue,
                "The program must end in an expression, which is its value.");

        // The statement's range, not the expression's: an expression node's range excludes the
        // parentheses around it, and `({ a: 1 })` must not become `(return ({ a: 1 }))`.
        var statement = source[last.Start..last.End].TrimEnd();
        if (statement.EndsWith(';')) statement = statement[..^1];
        var rewritten = source[..last.Start] + "return (" + statement + ");" + source[last.End..];

        // `async` only when the program actually awaits. `await` is legal solely inside an async
        // function however the parser was configured, so a program that reads a blob needs one.
        //
        // Only when needed, though, and that is not an optimization. An async function *absorbs* a
        // returned promise: `return blob.text()` with no `await` would resolve to the text, where
        // the spec says a promise left un-awaited as a result is `UTOS-E103`. Wrapping every
        // program would make that rule unobservable, silently turning an authoring mistake into a
        // value. A plain arrow lets the promise out, where the result converter refuses it by name.
        return HasAwait(program)
            ? ("(async () => {\n" + rewritten + "\n})()", true)
            : ("(() => {\n" + rewritten + "\n})()", false);
    }

    /// <summary>Whether any <c>await</c> appears in the program, at any depth.</summary>
    private static bool HasAwait(Acornima.Ast.Node node)
    {
        if (node is AwaitExpression) return true;

        foreach (var child in node.ChildNodes)
        {
            if (child is not null && HasAwait(child)) return true;
        }

        return false;
    }
}
