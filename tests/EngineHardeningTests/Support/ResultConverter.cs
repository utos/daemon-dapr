using Common.Workflow.Serialization;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;

namespace EngineHardeningTests.Support;

public sealed class ResultRejectedException(string message) : Exception(message);

/// <summary>
/// Turns a script result into plain CLR data. This is where a hostile result — a cycle, a getter, a
/// function, a Map, a giant tree — is stopped without recursing to death.
/// </summary>
public static class ResultConverter
{
    public static object? Convert(Engine engine, JsValue value, int maxDepth, int maxNodes)
    {
        var nodes = 0;
        return Convert(engine, value, 0, new HashSet<ObjectInstance>(ReferenceEqualityComparer.Instance), ref nodes, maxDepth, maxNodes);
    }

    private static object? Convert(Engine engine, JsValue value, int depth, HashSet<ObjectInstance> stack, ref int nodes, int maxDepth, int maxNodes)
    {
        if (++nodes > maxNodes) throw new ResultRejectedException($"result exceeds {maxNodes} nodes");
        if (depth > maxDepth) throw new ResultRejectedException($"result nested deeper than {maxDepth}");

        if (value.IsUndefined() || value.IsNull()) return null;
        if (value.IsBoolean()) return value.AsBoolean();
        if (value.IsString()) return value.AsString();
        if (value.IsNumber())
        {
            var number = value.AsNumber();
            if (!double.IsFinite(number)) throw new ResultRejectedException($"an expression produced a non-finite number ({number})");
            return NumberNarrowing.FromDouble(number);
        }

        if (value is Function) throw new ResultRejectedException("an expression cannot produce a function");

        if (value.IsArray())
        {
            var array = value.AsArray();
            if (!stack.Add(array)) throw new ResultRejectedException("result has a cycle");
            try
            {
                var length = (int)array.Length;
                var list = new List<object?>(Math.Min(length, 1024));
                for (var i = 0; i < length; i++)
                    list.Add(Convert(engine, array.Get(i), depth + 1, stack, ref nodes, maxDepth, maxNodes));
                return list;
            }
            finally { stack.Remove(array); }
        }

        if (value is ObjectInstance obj)
        {
            // Only plain data crosses back. A Map, Set or class instance would otherwise silently
            // become `{}` — a correctness failure before it is a safety one.
            var prototype = obj.Prototype;
            if (prototype is not null && !ReferenceEquals(prototype, engine.Intrinsics.Object.PrototypeObject))
                throw new ResultRejectedException($"an expression cannot produce a {obj.GetType().Name}; return plain objects and arrays");

            if (!stack.Add(obj)) throw new ResultRejectedException("result has a cycle");
            try
            {
                var result = new Dictionary<string, object?>();
                foreach (var key in obj.GetOwnPropertyKeys(Types.String))
                {
                    var descriptor = obj.GetOwnProperty(key);
                    // A getter here would run script AFTER evaluation returned, outside the limits.
                    if (descriptor.Get is not null || descriptor.Set is not null)
                        throw new ResultRejectedException("result contains an accessor property");
                    if (!descriptor.Enumerable) continue;
                    result[key.AsString()] = Convert(engine, descriptor.Value ?? JsValue.Undefined, depth + 1, stack, ref nodes, maxDepth, maxNodes);
                }
                return result;
            }
            finally { stack.Remove(obj); }
        }

        throw new ResultRejectedException($"an expression cannot produce a {value.Type}");
    }
}
