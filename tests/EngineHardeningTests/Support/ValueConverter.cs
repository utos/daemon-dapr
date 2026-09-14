using System.Text.Json;
using Jint;
using Jint.Native;

namespace EngineHardeningTests.Support;

public sealed class ScopeRejectedException(string message) : Exception(message);

/// <summary>Scope roots go in as copies — never as wrapped .NET objects — and frozen.</summary>
public static class ValueConverter
{
    // 2^53. A JS number is a double; a long beyond this would round silently on the way in,
    // where Scriban kept it exact. Refusing is the only honest answer: ids that large travel as
    // strings.
    private const long MaxSafeInteger = 9_007_199_254_740_992;

    /// <param name="freeze">The engine's <c>Object.freeze</c> intrinsic, or null to leave the copy mutable.</param>
    public static JsValue ToJs(Engine engine, object? value, JsValue? freeze = null) => value switch
    {
        null => JsValue.Null,
        string s => new JsString(s),
        bool b => b ? JsBoolean.True : JsBoolean.False,
        int i => JsNumber.Create(i),
        long l when Math.Abs(l) > MaxSafeInteger => throw new ScopeRejectedException(
            $"{l} cannot be represented exactly as a number; integers beyond 2^53 must be strings"),
        long l => JsNumber.Create((double)l),
        double d => JsNumber.Create(d),
        IReadOnlyDictionary<string, object?> map => Frozen(engine, ToJsObject(engine, map, freeze), freeze),
        IDictionary<string, object?> map => Frozen(engine, ToJsObject(engine, map.AsReadOnly(), freeze), freeze),
        System.Collections.IEnumerable items => Frozen(engine,
            new JsArray(engine, [.. items.Cast<object?>().Select(v => ToJs(engine, v, freeze))]), freeze),
        _ => throw new InvalidOperationException($"no JS mapping for {value.GetType()}"),
    };

    // One native call per object while the copy is built; no script statement is spent.
    private static JsValue Frozen(Engine engine, JsValue obj, JsValue? freeze)
    {
        if (freeze is not null) engine.Invoke(freeze, obj);
        return obj;
    }

    private static JsObject ToJsObject(Engine engine, IReadOnlyDictionary<string, object?> map, JsValue? freeze)
    {
        var obj = new JsObject(engine);
        foreach (var (key, item) in map)
            obj.Set(key, ToJs(engine, item, freeze));
        return obj;
    }
}

/// <summary>Builds a scope the way the daemon holds run data: dictionaries, lists, int/long/double.</summary>
public static class Scope
{
    public static Dictionary<string, object?> Of(params (string Name, string Json)[] roots) =>
        roots.ToDictionary(r => r.Name, r => Parse(r.Json));

    public static object? Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4096 });
        return Convert(document.RootElement);
    }

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
}
