using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace EngineHardeningTests.Support;

public sealed record EvaluationLimits(
    TimeSpan Timeout,
    int MaxStatements,
    long MemoryBytes,
    int MaxRecursion,
    uint MaxArraySize,
    TimeSpan RegexTimeout)
{
    public static EvaluationLimits Default { get; } = new(
        Timeout: TimeSpan.FromSeconds(1),
        MaxStatements: 100_000,
        MemoryBytes: 64L * 1024 * 1024,
        MaxRecursion: 64,
        MaxArraySize: 1_000_000,
        RegexTimeout: TimeSpan.FromMilliseconds(100));
}

/// <summary>
/// The reference posture from `plans/scripting-language.md` §6: the daemon runs any JavaScript and
/// every guarantee is a runtime one. Nothing here inspects the source before running it.
/// </summary>
public sealed class ReferenceEvaluator
{
    public EvaluationLimits Limits { get; init; } = EvaluationLimits.Default;
    public int MaxResultDepth { get; init; } = 200;
    public int MaxResultNodes { get; init; } = 1_000_000;

    /// <summary>
    /// Run the source through <see cref="Engine.PrepareScript"/> rather than <c>Evaluate(string)</c>.
    /// A prepared script compiles regex literals at parse time with the *parsing* options' timeout,
    /// so both timeouts have to be set — see <see cref="RegexTimeoutTests"/>.
    /// </summary>
    public bool PrepareScripts { get; init; }

    /// <summary>Off only to demonstrate what the option is load-bearing for.</summary>
    public bool StackOverflowGuard { get; init; } = true;

    public object? Evaluate(string source, IReadOnlyDictionary<string, object?> scope, CancellationToken cancellation = default)
    {
        using var session = Open(scope, cancellation);
        return session.Evaluate(source);
    }

    /// <summary>One engine, many expressions — one per activity, the shape the daemon uses.</summary>
    public Session Open(IReadOnlyDictionary<string, object?> scope, CancellationToken cancellation = default)
    {
        var engine = CreateEngine(cancellation);
        InjectScope(engine, scope);
        EngineLockdown.Apply(engine, scope.Keys);
        return new Session(this, engine);
    }

    public sealed class Session(ReferenceEvaluator owner, Engine engine) : IDisposable
    {
        public object? Evaluate(string source)
        {
            // Parse once: the grammar is checked on the tree Jint will run, then the same tree
            // decides where the completion value is so the program can be wrapped in its own
            // function scope (a top-level declaration in one expression is invisible to the next).
            var tree = new Acornima.Parser().ParseScript(source, strict: true);
            var violations = Grammar.Check(tree);
            if (violations.Count > 0) throw new GrammarRejectedException(violations);
            var program = ProgramWrapper.Wrap(source, tree);

            var result = owner.PrepareScripts
                ? engine.Evaluate(Engine.PrepareScript(program, strict: true,
                    options: new ScriptPreparationOptions
                    {
                        ParsingOptions = new ScriptParsingOptions { RegexTimeout = owner.Limits.RegexTimeout },
                    }))
                : engine.Evaluate(program);

            return ResultConverter.Convert(engine, result, owner.MaxResultDepth, owner.MaxResultNodes);
        }

        public void Dispose() => engine.Dispose();
    }

    private Engine CreateEngine(CancellationToken cancellation)
    {
        return new Engine(options =>
        {
            options.Strict();
            options.Culture(CultureInfo.InvariantCulture);
            options.LocalTimeZone(TimeZoneInfo.Utc);
            options.DisableStringCompilation();
            options.LimitRecursion(Limits.MaxRecursion);
            options.MaxArraySize(Limits.MaxArraySize);
            options.MaxJsonParseDepth(64);
            options.RegexTimeoutInterval(Limits.RegexTimeout);
            options.TimeoutInterval(Limits.Timeout);
            options.MaxStatements(Limits.MaxStatements);
            options.LimitMemory(Limits.MemoryBytes);
            options.CancellationToken(cancellation);
            // Off by default in Jint 4.x (~2% on deep recursion). Without it, native recursion in
            // JSON.stringify / Array.prototype.flat ends the process with a stack overflow that no
            // catch sees; with it, a catchable RangeError. Backported in the commit 4.16.2 was
            // built from (sebastienros/jint#4007).
            options.Constraints.StackOverflowGuard = StackOverflowGuard;
        });
    }

    /// <summary>
    /// Scope roots are deep-frozen host-side while being copied in (no script runs, no statement
    /// budget is spent) and bound as non-writable, non-configurable globals.
    /// </summary>
    private static void InjectScope(Engine engine, IReadOnlyDictionary<string, object?> scope)
    {
        HostLibrary.Install(engine);
        var freeze = engine.Evaluate("Object.freeze");
        foreach (var (name, value) in scope)
        {
            engine.SetValue(name, ValueConverter.ToJs(engine, value, freeze));
            engine.Execute($"Object.defineProperty(globalThis, {JsString(name)}, {{ writable: false, configurable: false }});");
        }
    }

    private static string JsString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
