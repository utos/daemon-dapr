using Utos.Workflows.V1;
using Common.Workflow.Proto;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Execution.Application.Models;
using Execution.Domain.Interpreter;
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

        var options = new TemplateEngineOptions();

        // `limits.materialization` is how a case asks for a small limit rather than writing a
        // multi-megabyte fixture to cross the real one.
        if (@case.TryGetProperty("limits", out var limits)
            && limits.TryGetProperty("materialization", out var materialization))
            options.MaterializationLimitBytes = materialization.GetInt64();

        var engine = new JintTemplateEngine(options, StoreFrom(@case));
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
                "collection" => ForEachCollection.Evaluate(engine, "{{ " + expression + " }}", scope),

                // The rendered text, not the TimeSpan: what the corpus pins is what the template
                // resolved to, and the text is also what history records so a replay reads the
                // same deadline. Through the daemon's own resolver, so this tests the timer path
                // rather than a copy of it.
                "duration" => DurationTemplate.Resolve(
                    engine, "{{ " + expression + " }}", scope, new SpecDurationParser()).Rendered,

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

        // A result that holds a blob is pinned as `wire`, because plain JSON cannot express one.
        //
        // A blob matches on size, media type, name, lastModified and *bytes*. The backing is
        // deliberately never compared: whether a result is inline or stored, and under what id and
        // offset, is the daemon's choice, and a corpus that pinned it would be pinning an
        // implementation detail rather than the spec.
        if (expect.TryGetProperty("wire", out var wire))
        {
            var expectedWire = WorkflowValue.Parser.ParseJson(wire.GetRawText());
            var producedWire = ValueMapping.ToWorkflowValue(actual);

            AssertSameValue(expectedWire, producedWire, StoreFrom(@case), "");
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

    /// <summary>
    /// Two <c>WorkflowValue</c>s are the same value. Everything but a blob compares structurally;
    /// a blob compares on what it <em>is</em> — its size, media type, name, modification time and
    /// bytes — and never on how it is stored.
    /// </summary>
    private static void AssertSameValue(
        WorkflowValue expected, WorkflowValue produced, IBlobReader? store, string path)
    {
        if (expected.KindCase == WorkflowValue.KindOneofCase.BlobValue)
        {
            var want = expected.BlobValue;
            var got = Assert.IsType<WorkflowValue>(produced).BlobValue;

            Assert.True(got is not null, $"expected a blob at '{path}'");
            Assert.Equal(want.Size, got!.Size);
            Assert.Equal(want.MediaType, got.MediaType);
            Assert.Equal(want.HasName ? want.Name : null, got.HasName ? got.Name : null);
            Assert.Equal(want.LastModified, got.LastModified);
            Assert.Equal(Bytes(want, store), Bytes(got, store));
            return;
        }

        Assert.Equal(expected.KindCase, produced.KindCase);

        switch (expected.KindCase)
        {
            case WorkflowValue.KindOneofCase.MapValue:
                Assert.Equal(
                    expected.MapValue.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal),
                    produced.MapValue.Fields.Keys.OrderBy(k => k, StringComparer.Ordinal));

                foreach (var (key, value) in expected.MapValue.Fields)
                    AssertSameValue(value, produced.MapValue.Fields[key], store, path + "/" + key);
                break;

            case WorkflowValue.KindOneofCase.ListValue:
                Assert.Equal(expected.ListValue.Values.Count, produced.ListValue.Values.Count);
                for (var i = 0; i < expected.ListValue.Values.Count; i++)
                    AssertSameValue(expected.ListValue.Values[i], produced.ListValue.Values[i], store, path + "/" + i);
                break;

            default:
                Assert.Equal(expected.ToString(), produced.ToString());
                break;
        }
    }

    /// <summary>A blob's bytes, fetched through the store when it is a handle.</summary>
    private static byte[] Bytes(Utos.Workflows.V1.Blob blob, IBlobReader? store)
    {
        if (blob.BackingCase == Utos.Workflows.V1.Blob.BackingOneofCase.Data)
            return blob.Data.ToByteArray();

        Assert.NotNull(store);
        return store!.Read(blob.ToDomain(), default);
    }

    /// <summary>
    /// The case's <c>store</c> sidecar as a blob reader: object id to base64 bytes. A stored blob's
    /// window — its offset and size — is applied here, which is what makes a stored blob and an
    /// inline one read identically, and is exactly what the daemon's own reader will do over S3.
    /// </summary>
    private static IBlobReader? StoreFrom(JsonElement @case)
    {
        if (!@case.TryGetProperty("store", out var store)) return null;

        var objects = store.EnumerateObject()
            .ToDictionary(o => o.Name, o => System.Convert.FromBase64String(o.Value.GetString()!));

        return new CorpusStore(objects);
    }

    private sealed class CorpusStore(IReadOnlyDictionary<string, byte[]> objects) : IBlobReader
    {
        public byte[] Read(Common.Workflow.WorkflowBlob blob, Common.SeedWork.ExecutionId runTree)
        {
            if (blob.Id is null || !objects.TryGetValue(blob.Id, out var bytes))
                throw new BlobUnreadableException($"No stored object '{blob.Id}'.");

            var offset = (int)blob.Offset;
            var length = (int)blob.Size;

            if (offset < 0 || length < 0 || offset + length > bytes.Length)
                throw new BlobUnreadableException(
                    $"'{blob.Id}' does not cover {offset}..{offset + length}.");

            return bytes.AsSpan(offset, length).ToArray();
        }
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

        // Two spellings, and the newer one is not a convenience. `scope` is plain JSON, which
        // cannot express a blob at all — a blob's type is its structure, so there is no object
        // shape that means "this is a blob". `scopeWire` is the protobuf JSON of a WorkflowMap,
        // where a blob is a kind rather than a key, and it is how every blob-bearing case is
        // written.
        if (@case.TryGetProperty("scopeWire", out var wire))
        {
            var map = WorkflowMap.Parser.ParseJson(wire.GetRawText());

            foreach (var (name, value) in map.ToRuntimeDictionary())
                scope = scope.With(name, value);

            return scope;
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
