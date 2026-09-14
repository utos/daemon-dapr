using Jint;
using Jint.Native;

namespace EngineHardeningTests.Support;

/// <summary>
/// The surface allow-list (`plans/scripting-language.md` §6.2): everything on <c>globalThis</c> and
/// on every built-in prototype that is not named here is deleted before user code runs, and what
/// remains is frozen. Allow, not deny — a new built-in in a future Jint is absent until listed.
/// </summary>
public static class EngineLockdown
{
    // Mutating array methods stay on purpose: the FREEZE decides what may be mutated (locals yes,
    // inputs no). The list only removes what can do unbounded native work or reach code.
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
            // Not repeat / padStart / padEnd: one call allocates outside the memory sampler.
        ],
        ["Object"] = ["hasOwnProperty", "toString", "valueOf", "constructor"],
        ["Number"] = ["toFixed", "toString", "valueOf", "constructor"],
        ["Boolean"] = ["toString", "valueOf", "constructor"],
        // No `constructor`: with it gone, a function value has no route back to Function.
        ["Function"] = ["call", "apply", "bind", "length", "name"],
    };

    // No `globalThis`: with `var`, `function` and `this` outside the language, nothing legitimate
    // needs the global object, and `globalThis.x = 1` was the one way an expression could leave
    // something behind for the next one on the same engine.
    private static readonly string[] Globals =
    [
        "undefined", "NaN", "Infinity",
        "Array", "String", "Number", "Boolean", "Object", "Math", "JSON", "Set", "Map",
        "parseInt", "parseFloat", "isNaN", "isFinite",
        "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI",
        "Symbol", // Symbol.iterator is what spread needs
        "utos",
    ];

    private static readonly string[] Statics =
    [
        "prototype", "name", "length",
        // Not Array.from: with `{length: n}` it manufactures n elements from a number, which is
        // the one thing a language without loops must not offer. Array.of only re-wraps arguments.
        "of", "isArray", "keys", "values", "entries", "fromEntries", "assign", "freeze",
        "hasOwn", "getPrototypeOf", "isInteger", "isFinite", "isNaN", "isSafeInteger",
        "parseFloat", "parseInt", "getOwnPropertyNames", "fromCharCode", "fromCodePoint",
        "MAX_SAFE_INTEGER", "MIN_SAFE_INTEGER", "EPSILON", "MAX_VALUE", "MIN_VALUE",
        "POSITIVE_INFINITY", "NEGATIVE_INFINITY", "NaN",
        // Math, everything but random
        "abs", "ceil", "floor", "round", "trunc", "sign", "min", "max", "pow", "sqrt", "cbrt",
        "log", "log2", "log10", "exp", "hypot", "sin", "cos", "tan", "atan", "atan2",
        "PI", "E", "LN2", "LN10", "LOG2E", "LOG10E", "SQRT2", "SQRT1_2",
        // JSON
        "parse", "stringify",
    ];

    public static void Apply(Engine engine, IEnumerable<string> scopeRoots)
    {
        var roots = scopeRoots.ToArray();
        var members = new JsObject(engine);
        foreach (var (proto, names) in PrototypeMembers)
            members.Set(proto, Strings(engine, names));

        engine.SetValue("__members", members);
        engine.SetValue("__globals", Strings(engine, Globals.Concat(roots)));
        engine.SetValue("__statics", Strings(engine, Statics));
        // The roots were frozen host-side on the way in; walking a 5,000-record payload in script
        // overflows the statement budget, so the freeze walk skips them.
        engine.SetValue("__skip", Strings(engine, roots));

        engine.Execute(Script);
    }

    private static JsArray Strings(Engine engine, IEnumerable<string> values) =>
        new(engine, [.. values.Select(v => (JsValue)new JsString(v))]);

    private const string Script = """
        (function () {
            // The global object, captured before the sweep removes the `globalThis` name itself.
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
            // Iterative, so depth cannot hit the recursion limit. Every expression runs in its own
            // function scope and `var`/`function` declarations are outside the language, so nothing
            // ever needs to define a global.
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
