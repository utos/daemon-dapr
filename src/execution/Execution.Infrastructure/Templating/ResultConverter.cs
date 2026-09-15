using Common.Workflow.Serialization;
using Execution.Domain.Templating;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;

namespace Execution.Infrastructure.Templating;

/// <summary>
/// Turns a program's value into plain CLR data (§ Values of the spec): null, bool, number,
/// string, and arrays and plain objects of those. Anything else is refused — a function, an
/// accessor property (which would run script after evaluation returned), a Map or Set (which
/// would otherwise silently become an empty object), a cycle, a tree beyond the limits. Numbers
/// take their CLR type from <see cref="NumberNarrowing"/>, the daemon's one rule for that.
/// </summary>
internal static class ResultConverter
{
    public static object? Convert(Engine engine, JsValue value, int maxDepth, int maxNodes)
    {
        var nodes = 0;
        return Convert(engine, value, 0, new HashSet<ObjectInstance>(ReferenceEqualityComparer.Instance), ref nodes, maxDepth, maxNodes);
    }

    private static object? Convert(Engine engine, JsValue value, int depth, HashSet<ObjectInstance> stack,
        ref int nodes, int maxDepth, int maxNodes)
    {
        if (++nodes > maxNodes)
            throw new TemplateException(EvaluationCodes.ResultNotPlainData, $"The result exceeds {maxNodes} nodes.");
        if (depth > maxDepth)
            throw new TemplateException(EvaluationCodes.ResultNotPlainData, $"The result is nested deeper than {maxDepth}.");

        if (value.IsUndefined() || value.IsNull()) return null;
        if (value.IsBoolean()) return value.AsBoolean();
        if (value.IsString()) return value.AsString();
        if (value.IsNumber())
        {
            var number = value.AsNumber();
            if (!double.IsFinite(number))
                throw new TemplateException(EvaluationCodes.NonFiniteNumber, $"An expression produced a non-finite number ({number}).");
            return NumberNarrowing.FromDouble(number);
        }

        if (value is Function)
            throw new TemplateException(EvaluationCodes.ResultNotPlainData, "An expression cannot produce a function.");

        if (value.IsArray())
        {
            var array = value.AsArray();
            if (!stack.Add(array))
                throw new TemplateException(EvaluationCodes.ResultNotPlainData, "The result contains a cycle.");
            try
            {
                var length = (int)array.Length;
                var list = new List<object?>(Math.Min(length, 1024));
                for (var i = 0; i < length; i++)
                    list.Add(Convert(engine, array.Get(i), depth + 1, stack, ref nodes, maxDepth, maxNodes));
                return list;
            }
            finally
            {
                stack.Remove(array);
            }
        }

        if (value is ObjectInstance obj)
        {
            var prototype = obj.Prototype;
            if (prototype is not null && !ReferenceEquals(prototype, engine.Intrinsics.Object.PrototypeObject))
                throw new TemplateException(EvaluationCodes.ResultNotPlainData, Refusal(obj));

            if (!stack.Add(obj))
                throw new TemplateException(EvaluationCodes.ResultNotPlainData, "The result contains a cycle.");
            try
            {
                var result = new Dictionary<string, object?>();
                foreach (var key in obj.GetOwnPropertyKeys(Types.String))
                {
                    var descriptor = obj.GetOwnProperty(key);
                    if (descriptor.Get is not null || descriptor.Set is not null)
                        throw new TemplateException(EvaluationCodes.ResultNotPlainData, "The result contains an accessor property.");
                    if (!descriptor.Enumerable) continue;
                    result[key.AsString()] = Convert(engine, descriptor.Value ?? JsValue.Undefined, depth + 1, stack, ref nodes, maxDepth, maxNodes);
                }

                return result;
            }
            finally
            {
                stack.Remove(obj);
            }
        }

        throw new TemplateException(EvaluationCodes.ResultNotPlainData, $"An expression cannot produce a {value.Type}.");
    }

    /// <summary>
    /// Says what to write instead for the values an author is most likely to return whole. Bytes
    /// and dates leave as the text the author chooses — explicit beats a silent conversion.
    /// </summary>
    private static string Refusal(ObjectInstance obj) => obj switch
    {
        Node.BufferInstance =>
            "An expression cannot produce a Buffer; return buffer.toString('base64'), toString('hex') or toString() instead.",
        _ when obj.GetType().Name.Contains("Date", StringComparison.Ordinal) =>
            "An expression cannot produce a Date; return date.toISOString() or date.getTime() instead.",
        Node.UrlObjects.UrlInstance or Node.UrlObjects.SearchParamsInstance =>
            "An expression cannot produce a URL; return url.href or url.toString() instead.",
        _ => $"An expression cannot produce a {obj.GetType().Name}; return plain objects and arrays."
    };
}
