using System.Text.Json;

namespace EngineHardeningTests.Support;

/// <summary>
/// Runs one expression against a fresh evaluator and reports what happened, so a test asserts on
/// the *class* of outcome. The scope is snapshotted before and after: a mutation of the host's data
/// is the one outcome no limit reports, so it is checked directly.
/// </summary>
public static class Attack
{
    public const string DefaultScope = """
        {"input": {"count": 1, "items": [3, 1, 2], "user": {"name": "x"}, "key": "constructor"},
         "env": {"SECRET": "s3cret"},
         "output": {"history": [{"id": "1"}]}}
        """;

    public sealed record Outcome(object? Value, Exception? Exception, bool HostMutated, TimeSpan Elapsed)
    {
        public T Threw<T>() where T : Exception
        {
            Assert.False(HostMutated, "the host's scope was mutated");
            return Assert.IsType<T>(Exception, exactMatch: false);
        }

        public object? Returned()
        {
            Assert.False(HostMutated, "the host's scope was mutated");
            Assert.Null(Exception);
            return Value;
        }
    }

    public static Outcome Run(string source, string? scopeJson = null, ReferenceEvaluator? evaluator = null, TimeSpan? cancelAfter = null)
    {
        var scope = ScopeFrom(scopeJson ?? DefaultScope);
        var before = JsonSerializer.Serialize(scope);
        using var cts = new CancellationTokenSource(cancelAfter ?? TimeSpan.FromSeconds(5));

        var started = DateTime.UtcNow;
        object? value = null;
        Exception? exception = null;
        try
        {
            value = (evaluator ?? new ReferenceEvaluator()).Evaluate(source, scope, cts.Token);
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        return new Outcome(value, exception, JsonSerializer.Serialize(scope) != before, DateTime.UtcNow - started);
    }

    public static Dictionary<string, object?> ScopeFrom(string json)
    {
        var root = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4096 }).RootElement;
        return root.EnumerateObject().ToDictionary(p => p.Name, p => Scope.Parse(p.Value.GetRawText()));
    }

    public static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// A scope whose <c>input.items</c> has <paramref name="count"/> numbers. In a language without
    /// loops the data is the only source of iteration count, so an attack that needs N steps has
    /// to be handed an N-element input — which is the point.
    /// </summary>
    public static string ScopeWithItems(int count, string extraInputFields = "") =>
        "{\"input\": {\"items\": [" + string.Join(",", Enumerable.Range(0, count)) + "]" + extraInputFields
        + "}, \"env\": {}, \"output\": {}}";
}
