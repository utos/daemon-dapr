using Acornima.Ast;

namespace EngineHardeningTests.Support;

/// <summary>
/// §6.4: one function scope per expression. The program is wrapped in an arrow IIFE so that its
/// top-level declarations are locals of that call rather than properties of the shared global
/// object, and its completion value — the final expression statement — becomes the function's
/// return value. Works on the tree the grammar check already produced, so the source is parsed
/// once.
/// </summary>
public static class ProgramWrapper
{
    public static string Wrap(string source, Program program)
    {
        var body = program.Body;

        if (body.Count > 0 && body[^1] is ExpressionStatement last)
        {
            // The statement's range, not the expression's: an expression node's range excludes
            // the parentheses around it, and `({ a: 1 })` must not become `(return ({ a: 1 }))`.
            var statement = source[last.Start..last.End].TrimEnd();
            if (statement.EndsWith(';')) statement = statement[..^1];
            source = source[..last.Start] + "return (" + statement + ");" + source[last.End..];
        }

        return "(() => {\n" + source + "\n})()";
    }
}
