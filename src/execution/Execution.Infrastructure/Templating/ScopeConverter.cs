using System.Text.Json;
using Common.Workflow;
using Execution.Domain.Templating;
using Execution.Infrastructure.Templating.Node;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Execution.Infrastructure.Templating;

/// <summary>
/// Copies a <see cref="TemplateScope"/> into an engine: plain JavaScript objects and arrays,
/// never wrapped .NET objects (which would expose their methods through reflection), deep-frozen
/// host-side while being built (no script runs, no statement budget is spent), and bound as
/// non-writable, non-configurable globals so nothing can rebind a root.
/// </summary>
internal static class ScopeConverter
{
    // 2^53. A JavaScript number is a double; a long beyond this would round silently on the way
    // in, where the previous engine carried it exactly. Refusing is the only honest answer.
    private const long MaxSafeInteger = 9_007_199_254_740_992;

    /// <param name="bufferPrototype">What a <c>byte[]</c> in scope — <c>response.body</c> — becomes.</param>
    public static void Inject(Engine engine, TemplateScope scope, ObjectInstance bufferPrototype)
    {
        var freeze = engine.Evaluate("Object.freeze");
        var context = new Context(engine, freeze, bufferPrototype);
        foreach (var (name, value) in scope.Values)
        {
            engine.SetValue(name, ToJs(context, value, name));
            engine.Execute("Object.defineProperty(globalThis, " + JsonSerializer.Serialize(name)
                           + ", { writable: false, configurable: false });");
        }
    }

    private sealed record Context(Engine Engine, JsValue Freeze, ObjectInstance BufferPrototype);

    private static JsValue ToJs(Context context, object? value, string path)
    {
        var engine = context.Engine;
        var freeze = context.Freeze;
        switch (value)
        {
            case null: return JsValue.Null;
            case string s: return new JsString(s);
            case bool b: return b ? JsBoolean.True : JsBoolean.False;
            case int i: return JsNumber.Create(i);

            // Before IEnumerable, which a byte[] also is. Read-only by construction, so it needs
            // no freeze; the same bytes back a Buffer created by an expression.
            case byte[] bytes: return new BufferInstance(engine, context.BufferPrototype, bytes);
            case long l when Math.Abs(l) > MaxSafeInteger:
                throw new TemplateException(EvaluationCodes.IntegerBeyondSafe,
                    $"`{path}` is {l}, which cannot be represented exactly as a number; integers beyond 2^53 must be strings.");
            case long l: return JsNumber.Create((double)l);
            case double d: return JsNumber.Create(d);
            case float f: return JsNumber.Create(f);
            case decimal m: return JsNumber.Create((double)m);

            // The run's ambient environment: a string → string map presented as one object.
            case TemplateNamespace ns:
                return Frozen(engine, Object(context, ns.Values.Select(kv => (kv.Key, (object?)kv.Value)), path), freeze);

            case ValueMap map:
                return Frozen(engine, Object(context, map.Fields.Select(kv => (kv.Key, kv.Value)), path), freeze);

            case IReadOnlyDictionary<string, object?> map:
                return Frozen(engine, Object(context, map.Select(kv => (kv.Key, kv.Value)), path), freeze);

            case IDictionary<string, object?> map:
                return Frozen(engine, Object(context, map.Select(kv => (kv.Key, kv.Value)), path), freeze);

            // An HTTP response body stays a JsonElement until it crosses a Dapr boundary
            // (HttpActivityHandler.ParseJsonResponse), so one can still reach a scope.
            case JsonElement json:
                return FromJson(engine, json, freeze, path);

            case System.Collections.IEnumerable items:
                return Frozen(engine, new JsArray(engine,
                    [.. items.Cast<object?>().Select((item, index) => ToJs(context, item, path + "[" + index + "]"))]), freeze);

            default:
                throw new TemplateException(EvaluationCodes.ScriptError,
                    $"`{path}` is a {value.GetType().Name}, which has no representation in an expression.");
        }
    }

    private static JsValue FromJson(Engine engine, JsonElement json, JsValue freeze, string path)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return JsValue.Null;
            case JsonValueKind.True: return JsBoolean.True;
            case JsonValueKind.False: return JsBoolean.False;
            case JsonValueKind.String: return new JsString(json.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                if (json.TryGetInt64(out var l) && Math.Abs(l) > MaxSafeInteger)
                    throw new TemplateException(EvaluationCodes.IntegerBeyondSafe,
                        $"`{path}` is {l}, which cannot be represented exactly as a number; integers beyond 2^53 must be strings.");
                return JsNumber.Create(json.GetDouble());
            case JsonValueKind.Array:
                return Frozen(engine, new JsArray(engine,
                    [.. json.EnumerateArray().Select((item, index) => FromJson(engine, item, freeze, path + "[" + index + "]"))]), freeze);
            default:
                var obj = new JsObject(engine);
                foreach (var property in json.EnumerateObject())
                    obj.Set(property.Name, FromJson(engine, property.Value, freeze, path + "." + property.Name));
                return Frozen(engine, obj, freeze);
        }
    }

    private static JsObject Object(Context context, IEnumerable<(string Key, object? Value)> fields, string path)
    {
        var obj = new JsObject(context.Engine);
        foreach (var (key, item) in fields)
            obj.Set(key, ToJs(context, item, path + "." + key));
        return obj;
    }

    private static JsValue Frozen(Engine engine, JsValue obj, JsValue freeze)
    {
        engine.Invoke(freeze, obj);
        return obj;
    }
}
