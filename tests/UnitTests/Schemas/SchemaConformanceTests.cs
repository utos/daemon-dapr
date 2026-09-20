using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Workflow;
using Execution.Domain.Schemas;
using Execution.Infrastructure.Schemas;

namespace UnitTests.Schemas;

/// <summary>
/// Runs the schema corpus of <c>utos/api</c> (<c>conformance/schema/</c>) against the evaluator
/// the daemon validates with. The corpus is the spec's definition of whether a value satisfies a
/// declaration — the defaults it fills and the failures it reports included — so this is the test
/// that says the daemon is a conformant validator rather than one that happens to agree today.
/// <para>
/// Vendored by hand into <c>Conformance/</c>, with the tag it came from in its README; the daemon
/// has no release pipeline that vendors the spec the way the SDK repos do.
/// </para>
/// </summary>
public class SchemaConformanceTests
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
    public void The_validator_produces_what_the_corpus_expects(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusRoot, name + ".json")));
        var @case = document.RootElement;

        var schema = ToValueMap(@case.GetProperty("schema"));
        var value = ToValueMap(@case.GetProperty("value"));
        var boundary = @case.TryGetProperty("boundary", out var declared)
            ? Boundary(declared.GetString()!)
            : SchemaBoundary.Input;

        var expect = @case.GetProperty("expect");
        var result = new JsonSchemaValidator().Validate(schema, value, boundary);

        Assert.Equal(expect.GetProperty("valid").GetBoolean(), result.IsValid);

        if (expect.TryGetProperty("errors", out var errors))
        {
            // The complete set, unordered. Reporting every failure rather than the first is the
            // point of the format, so an extra failure is as much a conformance break as a
            // missing one. keywordLocation is deliberately not asserted: more than one schema
            // location can legitimately produce the same failure.
            var expected = errors.EnumerateArray()
                .Select(e => (e.GetProperty("instanceLocation").GetString()!, e.GetProperty("keyword").GetString()!))
                .OrderBy(e => e, Comparer)
                .ToList();

            var actual = result.Failures
                .Select(f => (f.InstanceLocation, f.Keyword))
                .OrderBy(e => e, Comparer)
                .ToList();

            Assert.Equal(expected, actual);
        }

        // `filled` present asserts the value after defaults; absent asserts it is unchanged,
        // which is what an outbound boundary must always do.
        var expectedValue = expect.TryGetProperty("filled", out var filled) ? ToValueMap(filled) : value;
        Assert.Equal(Canonical(expectedValue), Canonical(result.Value));
    }

    /// <summary>
    /// Guards against the corpus silently disappearing — a wrong path would otherwise make the
    /// theory above vacuously pass with zero cases.
    /// </summary>
    [Fact]
    public void Corpus_is_present()
    {
        Assert.True(Directory.Exists(CorpusRoot), $"corpus directory not found: {CorpusRoot}");
        Assert.NotEmpty(Directory.EnumerateFiles(CorpusRoot, "*.json"));
    }

    private static SchemaBoundary Boundary(string name) => name switch
    {
        "input" => SchemaBoundary.Input,
        "env" => SchemaBoundary.Env,
        "output" => SchemaBoundary.Output,
        "emits" => SchemaBoundary.Emits,
        _ => throw new InvalidOperationException($"unknown boundary '{name}'")
    };

    private static IComparer<(string, string)> Comparer { get; } =
        Comparer<(string, string)>.Create((a, b) =>
        {
            var first = string.CompareOrdinal(a.Item1, b.Item1);
            return first != 0 ? first : string.CompareOrdinal(a.Item2, b.Item2);
        });

    /// <summary>Numbers as doubles throughout, so 80 and 80.0 compare equal.</summary>
    private static string Canonical(ValueMap value) =>
        Normalise(JsonNode.Parse(JsonSerializer.Serialize(value)))!.ToJsonString();

    private static JsonNode? Normalise(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<double>(out var number) => JsonValue.Create(number),
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Normalise(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalise).ToArray()),
        _ => node?.DeepClone()
    };

    private static ValueMap ToValueMap(JsonElement element) =>
        (ValueMap)ToObject(element)!;

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ValueMap.From(element.EnumerateObject()
            .Select(p => new KeyValuePair<string, object?>(p.Name, ToObject(p.Value)))),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static string CorpusRoot { get; } =
        Path.GetFullPath(Path.Combine(ThisDirectory(), "Conformance"));

    private static string ThisDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
