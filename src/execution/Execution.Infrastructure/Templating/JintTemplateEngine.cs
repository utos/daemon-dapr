using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima.Ast;
using Execution.Application.Models;
using Execution.Domain.Templating;
using Execution.Infrastructure.Templating.Node;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Options;

namespace Execution.Infrastructure.Templating;

/// <summary>
/// The expression engine of <c>api/docs/template-expressions.md</c>, on Jint. Every guarantee
/// of that document's § Runtime is made here: the surface is an allow-list, scope values are
/// deep-frozen copies, limits are invisible to script and fire as their own codes, native
/// recursion is an error rather than a process exit.
/// <para>
/// One Jint engine serves all the expressions evaluated against one <see cref="TemplateScope"/>
/// — the interpreter builds a scope per activity decision and evaluates every field of that
/// decision against it. Building an engine (options, Node globals, scope copy, lockdown) costs
/// ~2–3 ms; an expression on a built engine ~5 µs. Each expression runs in its own function
/// scope, so nothing one leaves behind reaches the next — except the counters behind
/// <c>Math.random()</c> and <c>crypto.randomUUID()</c>, which count across the activity's
/// expressions on purpose, so two never draw the same value. The scope's
/// <see cref="TemplateScope.Moment"/> is what <c>Date.now()</c> and those sequences derive from.
/// </para>
/// <para>
/// The grammar is not re-checked here. Every bundle the daemon executes went through
/// <c>Utos.Workflow.Validation</c> at load, where the static rules (<c>UTOS-E0##</c>) are
/// enforced on the same syntax tree this engine runs.
/// </para>
/// </summary>
public sealed class JintTemplateEngine : ITemplateEngine
{
    private const string Open = "{{";

    private readonly TemplateEngineOptions _options;
    private readonly ConditionalWeakTable<TemplateScope, Engine> _engines = new();
    private readonly ConcurrentDictionary<string, CompiledProgram> _programs = new(StringComparer.Ordinal);

    /// <param name="Awaits">
    /// Whether the program was wrapped in an async function, and so whether what comes back is a
    /// promise around its value. Only a program this engine made async is unwrapped: a promise a
    /// program produced <em>itself</em> and never awaited must reach the result converter, which
    /// refuses it as UTOS-E103.
    /// </param>
    private sealed record CompiledProgram(Prepared<Script> Script, bool Awaits);

    public JintTemplateEngine(IOptions<TemplateEngineOptions> options, IBlobReader? blobReader = null)
        : this(options.Value, blobReader)
    {
    }

    /// <param name="blobReader">
    /// How a stored blob's bytes are read. Null where there is no blob store — a supported
    /// deployment — and then an inline blob still works while a stored one is <c>UTOS-F103</c>,
    /// which is the same answer a deleted blob gets and deliberately so.
    /// </param>
    public JintTemplateEngine(TemplateEngineOptions options, IBlobReader? blobReader = null)
    {
        _options = options;
        _blobReader = blobReader;
    }

    private readonly IBlobReader? _blobReader;

    public bool EvaluateCondition(string condition, TemplateScope scope)
    {
        if (condition.Contains(Open, StringComparison.Ordinal))
            throw new TemplateException(EvaluationCodes.DelimitedCondition,
                "A condition is a bare expression; write `output.status === 'ready'`, not `{{ ... }}`.");

        var value = Run(condition, scope);
        if (value.IsBoolean()) return value.AsBoolean();

        throw new TemplateException(EvaluationCodes.ConditionNotBoolean,
            $"A condition must evaluate to true or false; `{condition}` evaluated to {Describe(value)}.");
    }

    public object? RenderTyped(string template, TemplateScope scope)
    {
        if (!template.Contains(Open, StringComparison.Ordinal)) return template;

        var segments = TemplateSplitter.Split(template);

        if (TemplateSplitter.IsWholeField(segments))
        {
            var program = TemplateSplitter.WholeFieldProgram(segments);
            var value = Run(program, scope);
            return ResultConverter.Convert(EngineFor(scope), value, _options.MaxResultDepth, _options.MaxResultNodes);
        }

        return Interpolate(segments, scope);
    }

    public string RenderToString(string template, TemplateScope scope)
    {
        if (!template.Contains(Open, StringComparison.Ordinal)) return template;
        return Interpolate(TemplateSplitter.Split(template), scope);
    }

    private string Interpolate(IReadOnlyList<TemplateSegment> segments, TemplateScope scope)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (!segment.IsExpression)
            {
                builder.Append(segment.Text);
                continue;
            }

            var value = Run(segment.Text, scope);
            builder.Append(ToText(EngineFor(scope), value));
        }

        return builder.ToString();
    }

    /// <summary>Rendering for interpolation: § Forms of the spec.</summary>
    private string ToText(Engine engine, JsValue value)
    {
        // Bytes have no text form an author did not choose. A blob interpolated into a larger
        // body, a URL or a header is `UTOS-E103` — the same refusal as a Buffer — and not a
        // rendering of the handle, which is what serializing it would silently produce.
        if (value is Node.BlobInstance)
            throw new TemplateException(EvaluationCodes.ResultNotPlainData,
                "A Blob cannot be rendered into a text field. Send it as a whole-field body, or "
                + "read it with `await blob.text()`.");

        if (value.IsUndefined() || value.IsNull()) return string.Empty;
        if (value.IsString()) return value.AsString();
        if (value.IsBoolean()) return value.AsBoolean() ? "true" : "false";
        if (value.IsNumber())
        {
            var number = value.AsNumber();
            if (!double.IsFinite(number))
                throw new TemplateException(EvaluationCodes.NonFiniteNumber, $"An expression produced a non-finite number ({number}).");
            // A whole number renders as digits: `5`, never `5.0` — and never `1E+16`, which is
            // what .NET's shortest round-trip form does above 10^15 and what JavaScript itself
            // does only above 10^21. Anything else takes the shortest form that round-trips.
            return number == Math.Floor(number) && Math.Abs(number) < 1e21
                ? number.ToString("F0", CultureInfo.InvariantCulture)
                : number.ToString("R", CultureInfo.InvariantCulture);
        }

        var converted = ResultConverter.Convert(engine, value, _options.MaxResultDepth, _options.MaxResultNodes);
        return JsonSerializer.Serialize(converted);
    }

    // ---- evaluation -----------------------------------------------------------------------

    private JsValue Run(string program, TemplateScope scope)
    {
        var engine = EngineFor(scope);
        var prepared = _programs.GetOrAdd(program, Prepare);

        try
        {
            var value = engine.Evaluate(prepared.Script);

            // Unwrapped only when *this engine* made the program async (see ProgramWrapper). A
            // promise the program produced itself and never awaited is not unwrapped: it must
            // reach the result converter, which refuses it as UTOS-E103, because a promise is not
            // a value.
            return prepared.Awaits ? value.UnwrapIfPromise() : value;
        }
        catch (PromiseRejectedException ex) when (Inner(ex) is TemplateException template)
        {
            throw template;
        }
        catch (StatementsCountOverflowException ex)
        {
            throw Limit(EvaluationCodes.StatementBudget, $"The expression exceeded the statement budget ({_options.MaxStatements}).", ex);
        }
        catch (MemoryLimitExceededException ex) when (ex.Message.Contains("array size", StringComparison.OrdinalIgnoreCase))
        {
            throw Limit(EvaluationCodes.ArraySize, $"The expression built an array larger than {_options.MaxArraySize} elements.", ex);
        }
        catch (MemoryLimitExceededException ex)
        {
            throw Limit(EvaluationCodes.Memory, $"The expression exceeded the memory limit ({_options.MemoryLimitBytes} bytes).", ex);
        }
        // Before TimeoutException, which it derives from.
        catch (RegexMatchTimeoutException ex)
        {
            throw Limit(EvaluationCodes.RegexTimeout, $"A regular expression exceeded its time limit ({_options.RegexTimeoutMilliseconds} ms).", ex);
        }
        catch (TimeoutException ex)
        {
            throw Limit(EvaluationCodes.Timeout, $"The expression exceeded the time limit ({_options.TimeoutMilliseconds} ms).", ex);
        }
        catch (RecursionDepthOverflowException ex)
        {
            throw Limit(EvaluationCodes.Recursion, $"The expression exceeded the recursion limit ({_options.MaxRecursionDepth}).", ex);
        }
        catch (ExecutionCanceledException ex)
        {
            throw Limit(EvaluationCodes.Cancelled, "The evaluation was cancelled.", ex);
        }
        catch (JavaScriptException ex) when (ex.Message.Contains("Maximum call stack size exceeded", StringComparison.Ordinal))
        {
            throw Limit(EvaluationCodes.NativeStack, "The expression recursed too deeply (native stack exhausted).", ex);
        }
        catch (JavaScriptException ex) when (ex.Message.Contains("depth level of JSON", StringComparison.OrdinalIgnoreCase))
        {
            throw Limit(EvaluationCodes.JsonDepth, $"JSON.parse input nested deeper than {_options.MaxJsonParseDepth}.", ex);
        }
        catch (JavaScriptException ex)
        {
            throw new TemplateException(EvaluationCodes.ScriptError, "The expression threw: " + ex.Message, ex);
        }
    }

    private static TemplateException Limit(string code, string message, Exception inner) =>
        new(code, message, inner);

    private CompiledProgram Prepare(string program)
    {
        var (wrapped, awaits) = ProgramWrapper.Wrap(program);
        return new CompiledProgram(Engine.PrepareScript(wrapped, strict: true, options: new ScriptPreparationOptions
        {
            // A prepared script compiles its regex literals now, with THIS timeout; the engine's
            // RegexTimeoutInterval covers matches at run time only.
            ParsingOptions = new ScriptParsingOptions
            {
                RegexTimeout = TimeSpan.FromMilliseconds(_options.RegexTimeoutMilliseconds),
            },
        }), awaits);
    }

    // ---- engines --------------------------------------------------------------------------

    private Engine EngineFor(TemplateScope scope) => _engines.GetValue(scope, Build);

    private Engine Build(TemplateScope scope)
    {
        var engine = new Engine(options =>
        {
            options.Strict();
            options.Culture(CultureInfo.InvariantCulture);
            options.LocalTimeZone(TimeZoneInfo.Utc);
            options.DisableStringCompilation();
            options.LimitRecursion(_options.MaxRecursionDepth);
            options.MaxArraySize(_options.MaxArraySize);
            options.MaxJsonParseDepth(_options.MaxJsonParseDepth);
            options.RegexTimeoutInterval(TimeSpan.FromMilliseconds(_options.RegexTimeoutMilliseconds));
            options.TimeoutInterval(TimeSpan.FromMilliseconds(_options.TimeoutMilliseconds));
            options.MaxStatements(_options.MaxStatements);
            options.LimitMemory(_options.MemoryLimitBytes);
            // Off by default in Jint 4.x. Without it, native recursion in JSON.stringify / flat
            // ends the process with a stack overflow that nothing catches; with it, a RangeError
            // reported as UTOS-E114. sebastienros/jint#3877, backported as #4007.
            options.Constraints.StackOverflowGuard = true;
        });

        // Order matters: the Node globals before the scope (a Buffer in scope needs its prototype),
        // then Blob and File — which need the Buffer prototype, since `bytes()` produces one — and
        // all of them before the lockdown, which deletes the temporaries they leave and freezes
        // everything they installed.
        var node = NodeGlobals.Install(engine, scope.Moment);

        var blobs = BlobObjects.Install(
            engine, node.BufferPrototype, _blobReader,
            _options.MaterializationLimitBytes, scope.Moment.Instant, scope.RunTree);

        ScopeConverter.Inject(engine, scope, node.BufferPrototype, blobs);
        EngineLockdown.Apply(engine, scope.Values.Keys);
        return engine;
    }

    /// <summary>The exception a rejected promise carries, when it carries a CLR one.</summary>
    private static Exception? Inner(PromiseRejectedException ex) =>
        ex.RejectedValue is Jint.Runtime.Interop.ObjectWrapper wrapper ? wrapper.Target as Exception : ex.InnerException;

    private static string Describe(JsValue value)
    {
        if (value.IsUndefined()) return "undefined";
        if (value.IsNull()) return "null";
        if (value.IsString()) return "the string " + JsonSerializer.Serialize(value.AsString());
        if (value.IsNumber()) return "the number " + value.AsNumber().ToString("R", CultureInfo.InvariantCulture);
        return "a " + value.Type.ToString().ToLowerInvariant();
    }
}

/// <summary>The evaluation codes of <c>api/docs/template-expressions.md</c>.</summary>
public static class EvaluationCodes
{
    public const string ConditionNotBoolean = "UTOS-E101";
    public const string NonFiniteNumber = "UTOS-E102";
    public const string ResultNotPlainData = "UTOS-E103";
    public const string IntegerBeyondSafe = "UTOS-E104";
    public const string CollectionNotArray = "UTOS-E105";
    public const string StatementBudget = "UTOS-E110";
    public const string Memory = "UTOS-E111";
    public const string Timeout = "UTOS-E112";
    public const string Recursion = "UTOS-E113";
    public const string NativeStack = "UTOS-E114";
    public const string ArraySize = "UTOS-E115";
    public const string JsonDepth = "UTOS-E116";
    public const string RegexTimeout = "UTOS-E117";
    public const string Cancelled = "UTOS-E118";
    public const string ScriptError = "UTOS-E120";

    // Static rules the validator normally catches at load; reported here only for a bundle that
    // reached evaluation without passing through it.
    public const string SyntaxError = "UTOS-E060";
    public const string DelimitedCondition = "UTOS-E061";
    public const string Unclosed = "UTOS-E062";
    public const string NoValue = "UTOS-E063";
}
