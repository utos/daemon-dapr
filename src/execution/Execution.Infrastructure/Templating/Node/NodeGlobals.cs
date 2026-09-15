using System.Globalization;
using Execution.Domain.Templating;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Execution.Infrastructure.Templating.Node;

/// <summary>
/// The Node.js globals of <c>api/docs/template-expressions.md</c> § Node.js globals, installed on
/// an engine before its lockdown: <c>Buffer</c>, <c>crypto</c>, <c>URL</c>, <c>URLSearchParams</c>,
/// and the deterministic <c>Date</c> and <c>Math.random</c>.
/// <para>
/// One instance per engine, because the two counters — the <i>n</i> of the <i>n</i>-th
/// <c>Math.random()</c> and <c>randomUUID()</c> — are per activity evaluation, and an engine is
/// exactly that. Everything the spec derives from the seed is derived in
/// <see cref="Deterministic"/>, where the derivation is pinned by the evaluation corpus.
/// </para>
/// </summary>
internal sealed class NodeGlobals
{
    private readonly Engine _engine;
    private readonly EvaluationMoment _moment;
    private int _randomCalls;
    private int _uuidCalls;

    private NodeGlobals(Engine engine, EvaluationMoment moment)
    {
        _engine = engine;
        _moment = moment;
        BufferPrototype = BufferObjects.CreatePrototype(engine);
    }

    /// <summary>What a <c>Buffer</c> instance inherits from; the scope converter needs it too.</summary>
    public ObjectInstance BufferPrototype { get; }

    public static NodeGlobals Install(Engine engine, EvaluationMoment moment)
    {
        var globals = new NodeGlobals(engine, moment);

        engine.SetValue("Buffer", BufferObjects.CreateConstructor(engine, globals.BufferPrototype));
        engine.SetValue("crypto", CryptoObjects.Create(engine, globals.BufferPrototype, globals.NextUuid));
        UrlObjects.Install(engine);

        // Time and randomness, replaced rather than removed: the captured instant and the
        // seed-derived sequence stand in for the wall clock and the PRNG. `__now` and `__random`
        // are removed again by the lockdown sweep; the closures below keep what they need.
        engine.SetValue("__now", (double)moment.Instant.ToUnixTimeMilliseconds());
        engine.SetValue("__random", new ClrFunction(engine, "random", (_, _) => JsNumber.Create(globals.NextRandom())));
        engine.Execute(DatePrelude);

        return globals;
    }

    private double NextRandom() => Deterministic.Random(_moment.Seed, _randomCalls++);

    private string NextUuid() => Deterministic.Uuid(_moment.Seed, _uuidCalls++);

    // `Date` is replaced by a function that supplies the captured instant when called with no
    // argument and otherwise defers to the real constructor, sharing its prototype so every Date
    // method works and `new Date(x)` instances are indistinguishable. The real constructor is
    // unreachable afterwards: the prototype's `constructor` is rebound to the replacement, and
    // Function.prototype.constructor is gone once the lockdown runs. `Date()` called as a
    // function returns the instant as a string, as Node's does the wall clock.
    private const string DatePrelude = """
        (function () {
            // Through globalThis: the declaration below is hoisted over this whole scope, so a
            // bare `Date` here would already be the replacement.
            const RealDate = globalThis.Date;
            const now = __now;
            function Date(...args) {
                if (new.target === undefined) return new RealDate(now).toString();
                return args.length === 0 ? new RealDate(now) : new RealDate(...args);
            }
            Date.prototype = RealDate.prototype;
            RealDate.prototype.constructor = Date;
            Date.now = function now_() { return now; };
            Date.parse = RealDate.parse;
            Date.UTC = RealDate.UTC;
            globalThis.Date = Date;
            Math.random = __random;
        })();
        """;
}
