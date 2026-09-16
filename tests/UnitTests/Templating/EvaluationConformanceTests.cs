using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Execution.Application.Models;
using Execution.Domain.Templating;
using Execution.Infrastructure.Templating;

namespace UnitTests.Templating;

/// <summary>
/// Runs the evaluation corpus of <c>utos/api</c> (<c>conformance/evaluation/</c>) against the
/// engine the daemon runs. The corpus is the spec's definition of what an expression evaluates
/// to — the deterministic derivations included — so this is the test that says the daemon is a
/// conformant evaluator rather than one that happens to agree today.
/// <para>
/// Vendored by hand into <c>Conformance/</c>, with the tag it came from in its README; the
/// daemon has no release pipeline that vendors the spec the way the SDK repos do.
/// </para>
/// </summary>
public class EvaluationConformanceTests
{
    public static TheoryData<string> Cases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(CorpusRoot, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                data.Add(Path.GetFileNameWithoutExtension(file));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_engine_produces_what_the_corpus_expects(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusRoot, name + ".json")));
        var @case = document.RootElement;

        var scope = ScopeFrom(@case);
        var engine = new JintTemplateEngine(new TemplateEngineOptions());
        var form = @case.GetProperty("form").GetString();
        var expression = @case.GetProperty("expression").GetString()!;
        var expect = @case.GetProperty("expect");

        // `before` runs on the same engine and scope, as two expressions of one activity do.
        if (@case.TryGetProperty("before", out var before))
            engine.RenderTyped("{{ " + before.GetString() + " }}", scope);

        object? actual;
        try
        {
            actual = form switch
            {
                "condition" => engine.EvaluateCondition(expression, scope),
                "value" => engine.RenderTyped("{{ " + expression + " }}", scope),
                "text" => engine.RenderToString("{{ " + expression + " }}", scope),
                _ => throw new InvalidOperationException($"unknown form '{form}'")
            };
        }
        catch (TemplateException exception)
        {
            Assert.True(expect.TryGetProperty("error", out var code),
                $"expected a value but the engine refused with {exception.Code}: {exception.Message}");
            Assert.Equal(code.GetString(), exception.Code);
            return;
        }

        if (expect.TryGetProperty("error", out var expectedCode))
            Assert.Fail($"expected {expectedCode.GetString()} but the engine produced {JsonSerializer.Serialize(actual)}");

        if (expect.TryGetProperty("omitted", out var omitted) && omitted.GetBoolean())
        {
            // A whole-result `undefined` comes back as null here; the field's omission is the
            // renderer's job, one level up.
            Assert.Null(actual);
            return;
        }

        // Compared as JSON, so an int 2 and a double 2 are the same value, as the corpus README
        // says they are.
        var expected = JsonNode.Parse(expect.GetProperty("value").GetRawText());
        var produced = JsonNode.Parse(JsonSerializer.Serialize(actual));
        Assert.True(JsonNode.DeepEquals(Normalise(expected), Normalise(produced)),
            $"expected {expected?.ToJsonString()} but got {produced?.ToJsonString()}");
    }

    [Fact]
    public void The_corpus_is_present()
    {
        Assert.True(Directory.Exists(CorpusRoot), $"corpus not found: {CorpusRoot}");
        Assert.NotEmpty(Directory.EnumerateFiles(CorpusRoot, "*.json"));
    }

    /// <summary>The scope as the daemon would hold it: dictionaries, lists, CLR primitives, bytes.</summary>
    private static TemplateScope ScopeFrom(JsonElement @case)
    {
        var scope = new TemplateScope();

        if (@case.TryGetProperty("clock", out var clock) || @case.TryGetProperty("seed", out _))
        {
            var instant = clock.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(clock.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : DateTimeOffset.UnixEpoch;
            var seed = @case.TryGetProperty("seed", out var seedValue) ? Guid.Parse(seedValue.GetString()!) : Guid.Empty;
            scope = scope.WithMoment(new EvaluationMoment(instant, seed));
        }

        foreach (var property in @case.GetProperty("scope").EnumerateObject())
        {
            scope = property.Name == "env"
                ? scope.WithEnv(property.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty))
                : scope.With(property.Name, Convert(property.Value));
        }

        return scope;
    }

    private static object? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // The corpus writes a Buffer in scope as { "$buffer": "<base64>" }.
                if (element.TryGetProperty("$buffer", out var buffer) && element.EnumerateObject().Count() == 1)
                    return System.Convert.FromBase64String(buffer.GetString()!);
                return element.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value));
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(Convert).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number when element.TryGetInt32(out var i):
                return i;
            case JsonValueKind.Number when element.TryGetInt64(out var l):
                return l;
            case JsonValueKind.Number:
                return element.GetDouble();
            default:
                return null;
        }
    }

    /// <summary>Numbers as doubles throughout, so 2 and 2.0 compare equal.</summary>
    private static JsonNode? Normalise(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<double>(out var number) => JsonValue.Create(number),
        JsonObject obj => new JsonObject(obj.Select(kv => KeyValuePair.Create(kv.Key, Normalise(kv.Value)))),
        JsonArray array => new JsonArray(array.Select(Normalise).ToArray()),
        _ => node?.DeepClone()
    };

    private static string CorpusRoot { get; } =
        Path.GetFullPath(Path.Combine(ThisDirectory(), "Conformance"));

    private static string ThisDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
