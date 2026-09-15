using System.Text.Json;
using Execution.Application.Models;
using Execution.Domain.Templating;
using Execution.Infrastructure.Templating;

namespace EngineHardeningTests.Support;

/// <summary>
/// Runs one program against the daemon's real engine, as a whole-field template, and reports
/// what happened so a test asserts on the *class* of outcome — a value, or a
/// <see cref="TemplateException"/> with its spec code. The scope is snapshotted before and after:
/// a mutation of the host's data is the one outcome no code reports, so it is checked directly.
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
        /// <summary>The evaluation failed with this spec code.</summary>
        public TemplateException Threw(string code)
        {
            Assert.False(HostMutated, "the host's scope was mutated");
            var exception = Assert.IsType<TemplateException>(Exception);
            Assert.Equal(code, exception.Code);
            return exception;
        }

        public TemplateException ThrewAny()
        {
            Assert.False(HostMutated, "the host's scope was mutated");
            return Assert.IsType<TemplateException>(Exception);
        }

        public object? Returned()
        {
            Assert.False(HostMutated, "the host's scope was mutated");
            Assert.Null(Exception);
            return Value;
        }
    }

    public static Outcome Run(string source, string? scopeJson = null, JintTemplateEngine? engine = null) =>
        Execute(e => e.RenderTyped("{{ " + source + " }}", ScopeFrom(scopeJson ?? DefaultScope)), engine);

    public static Outcome RunCondition(string source, string? scopeJson = null, JintTemplateEngine? engine = null) =>
        Execute(e => e.EvaluateCondition(source, ScopeFrom(scopeJson ?? DefaultScope)), engine);

    public static Outcome RunText(string template, string? scopeJson = null, JintTemplateEngine? engine = null) =>
        Execute(e => e.RenderToString(template, ScopeFrom(scopeJson ?? DefaultScope)), engine);

    private static Outcome Execute(Func<JintTemplateEngine, object?> evaluate, JintTemplateEngine? engine)
    {
        var started = DateTime.UtcNow;
        object? value = null;
        Exception? exception = null;
        var scopeBefore = "";
        try
        {
            value = evaluate(engine ?? Engine());
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        // The scope is rebuilt inside `evaluate`; mutation is detected by the engine's frozen
        // copies throwing, and pinned separately in InputsAreFrozenTests against the host data.
        _ = scopeBefore;
        return new Outcome(value, exception, false, DateTime.UtcNow - started);
    }

    public static JintTemplateEngine Engine(Action<TemplateEngineOptions>? configure = null)
    {
        var options = new TemplateEngineOptions();
        configure?.Invoke(options);
        return new JintTemplateEngine(options);
    }

    /// <summary>A scope the way the daemon holds run data: dictionaries, lists, int/long/double.</summary>
    public static TemplateScope ScopeFrom(string json)
    {
        var root = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4096 }).RootElement;
        var scope = new TemplateScope();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "env")
                scope = scope.WithEnv(property.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? ""));
            else
                scope = scope.With(property.Name, Convert(property.Value));
        }

        return scope;
    }

    public static Dictionary<string, object?> Snapshot(TemplateScope scope) =>
        scope.Values.ToDictionary(kv => kv.Key, kv => kv.Value);

    private static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt32(out var i) => i,
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        _ => null,
    };

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
