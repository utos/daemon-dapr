using Jint;
using Jint.Native;

namespace Execution.Infrastructure.Templating;

/// <summary>
/// The run-time surface of <c>api/docs/template-expressions.md</c> § Surface, as an allow-list:
/// everything on the global object and on every built-in prototype that is not named here is
/// deleted before user code runs, and what remains is frozen — including the global object
/// itself, which no program can name. A new built-in in a future Jint is absent until listed.
/// </summary>
internal static class EngineLockdown
{
    // Mutating array methods stay on purpose: the FREEZE decides what may be mutated (locals
    // yes, scope values no). The list only removes what does unbounded native work or reaches
    // code — repeat/padStart/padEnd allocate outside the memory sampler, Array.from manufactures
    // elements from a number, Function.prototype.constructor leads back to Function.
    private static readonly Dictionary<string, string[]> PrototypeMembers = new()
    {
        ["Array"] =
        [
            "map", "filter", "find", "findIndex", "findLast", "findLastIndex", "some", "every",
            "flatMap", "flat", "reduce", "reduceRight", "includes", "indexOf", "lastIndexOf", "slice",
            "concat", "join", "at", "toSorted", "toReversed", "toSpliced", "with", "forEach", "keys",
            "values", "entries", "length", "constructor",
            "push", "pop", "shift", "unshift", "splice", "sort", "reverse", "fill", "copyWithin",
        ],
        ["String"] =
        [
            "includes", "indexOf", "lastIndexOf", "slice", "at", "trim", "trimStart", "trimEnd",
            "toLowerCase", "toUpperCase", "startsWith", "endsWith", "split", "replace", "replaceAll",
            "substring", "charAt", "charCodeAt", "codePointAt", "concat", "length", "constructor",
            "normalize", "match", "matchAll", "search", "localeCompare",
        ],
        ["Object"] = ["hasOwnProperty", "toString", "valueOf", "constructor"],
        ["Number"] = ["toFixed", "toString", "valueOf", "constructor"],
        ["Boolean"] = ["toString", "valueOf", "constructor"],
        ["Function"] = ["call", "apply", "bind", "length", "name"],
    };

    // The Node globals — Buffer, crypto, URL, URLSearchParams — are host objects whose members
    // are exactly what NodeGlobals put there, so they are kept whole and only frozen; Date keeps
    // its standard prototype, with its constructor and `now` replaced before this runs.
    private static readonly string[] Globals =
    [
        "undefined", "NaN", "Infinity",
        "Array", "String", "Number", "Boolean", "Object", "Math", "JSON", "Set", "Map", "Symbol",
        "parseInt", "parseFloat", "isNaN", "isFinite",
        "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI",
        "Buffer", "crypto", "Date", "URL", "URLSearchParams",
    ];

    // `random` is Math's, replaced by the seed-derived sequence before this list is applied.
    private static readonly string[] Statics =
    [
        "prototype", "name", "length",
        "of", "isArray", "keys", "values", "entries", "fromEntries", "assign", "freeze",
        "hasOwn", "getPrototypeOf", "isInteger", "isFinite", "isNaN", "isSafeInteger",
        "parseFloat", "parseInt", "getOwnPropertyNames", "fromCharCode", "fromCodePoint",
        "MAX_SAFE_INTEGER", "MIN_SAFE_INTEGER", "EPSILON", "MAX_VALUE", "MIN_VALUE",
        "POSITIVE_INFINITY", "NEGATIVE_INFINITY", "NaN",
        "abs", "ceil", "floor", "round", "trunc", "sign", "min", "max", "pow", "sqrt", "cbrt",
        "log", "log2", "log10", "exp", "hypot", "sin", "cos", "tan", "atan", "atan2", "random",
        "PI", "E", "LN2", "LN10", "LOG2E", "LOG10E", "SQRT2", "SQRT1_2",
        "parse", "stringify",
    ];

    /// <param name="scopeRoots">Kept as globals but skipped by the freeze walk: they were frozen host-side on the way in.</param>
    public static void Apply(Engine engine, IEnumerable<string> scopeRoots)
    {
        var roots = scopeRoots.ToArray();
        var members = new JsObject(engine);
        foreach (var (prototype, names) in PrototypeMembers)
            members.Set(prototype, Strings(engine, names));

        engine.SetValue("__members", members);
        engine.SetValue("__globals", Strings(engine, Globals.Concat(roots)));
        engine.SetValue("__statics", Strings(engine, Statics));
        engine.SetValue("__skip", Strings(engine, roots));

        engine.Execute(Script);
    }

    private static JsArray Strings(Engine engine, IEnumerable<string> values) =>
        new(engine, [.. values.Select(value => (JsValue)new JsString(value))]);

    // Runs once per engine, before any user code, under the engine's own limits.
    private const string Script = """
        (function () {
            const G = globalThis;
            const members = __members, statics = new Set(__statics), skip = new Set(__skip);
            const keep = new Set(__globals);
            const names = Object.getOwnPropertyNames, freezeOne = Object.freeze, protoOf = Object.getPrototypeOf;
            const push = Array.prototype.push;

            for (const name of names(G)) {
                if (!keep.has(name)) { try { delete G[name]; } catch (e) {} }
            }

            const protos = { Array: Array.prototype, String: String.prototype, Object: Object.prototype,
                             Number: Number.prototype, Boolean: Boolean.prototype,
                             Function: protoOf(function () {}) };
            for (const key of Object.keys(protos)) {
                const allow = new Set(members[key] || []);
                for (const name of names(protos[key])) {
                    if (!allow.has(name)) { try { delete protos[key][name]; } catch (e) {} }
                }
            }

            for (const C of [Array, String, Number, Boolean, Object, Set, Map, Math, JSON]) {
                for (const name of names(C)) {
                    if (!statics.has(name)) { try { delete C[name]; } catch (e) {} }
                }
            }

            // Freeze everything reachable from every global, then the global object itself.
            // Iterative, so depth cannot hit the recursion limit.
            const seen = new Set([G]);
            const stack = [];
            for (const name of names(G)) {
                if (skip.has(name)) continue;
                let v; try { v = G[name]; } catch (e) { continue; }
                push.call(stack, v);
            }
            while (stack.length > 0) {
                const o = stack.pop();
                if (o === null || (typeof o !== 'object' && typeof o !== 'function')) continue;
                if (seen.has(o)) continue;
                seen.add(o);
                try { freezeOne(o); } catch (e) {}
                for (const n of names(o)) {
                    let v; try { v = o[n]; } catch (e) { continue; }
                    push.call(stack, v);
                }
                const p = protoOf(o);
                if (p) push.call(stack, p);
            }
            freezeOne(G);
        })();
        """;
}
