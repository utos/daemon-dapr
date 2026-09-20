using Execution.Domain.Templating;

namespace Execution.Domain.Interpreter;

/// <summary>
/// A promise branch's <c>forEach.collection</c>, evaluated: a whole-field value that must be an
/// array (<c>api/docs/template-expressions.md</c> § Where expressions appear). A string, an object,
/// a number, <c>null</c> or <c>undefined</c> is <c>UTOS-E105</c> — an authoring error, not an
/// empty expansion, and never a string iterated character by character.
/// <para>
/// Its own function so that the interpreter and the evaluation conformance corpus's
/// <c>collection</c> form run the same check, rather than the corpus testing a copy of it.
/// </para>
/// </summary>
public static class ForEachCollection
{
    public const string NotAnArrayCode = "UTOS-E105";

    public static List<object?> Evaluate(ITemplateEngine templateEngine, string collection, TemplateScope scope)
    {
        var rendered = templateEngine.RenderTyped(collection, scope);
        if (rendered is not IEnumerable<object?> items || rendered is string)
            throw new TemplateException(NotAnArrayCode,
                $"forEach collection `{collection}` must evaluate to an array.");

        return items.ToList();
    }
}
