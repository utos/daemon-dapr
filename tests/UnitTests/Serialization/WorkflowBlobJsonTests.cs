using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Workflow;
using Common.Workflow.Serialization;

namespace UnitTests.Serialization;

/// <summary>
/// A blob inside a run value, and the escaping that keeps the encoding unambiguous.
/// <para>
/// This is the piece that makes § Moving blobs true at all. A blob is a kind of value, but every hop
/// a value takes in this daemon is JSON — a Dapr activity input, an event, a JSON column — and JSON
/// has no kind for it. Without an encoding a blob reached the far side as an ordinary map, which is
/// silent: <c>response.body</c> kept working because it is a declared field, while the same blob in
/// an activity's <c>input</c> or a run's result quietly stopped being a blob.
/// </para>
/// </summary>
public class WorkflowBlobJsonTests
{
    /// <summary>A carrier shaped like the real ones: an <c>object</c> leaf and a declared map.</summary>
    private sealed record Carrier(
        object? Value,
        [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
        Dictionary<string, object>? Map = null);

    private static string Write(object? value) =>
        JsonSerializer.Serialize(new Carrier(value), WorkflowJson.Options);

    private static object? RoundTrip(object? value) =>
        JsonSerializer.Deserialize<Carrier>(Write(value), WorkflowJson.Options)!.Value;

    public class TheEncoding
    {
        [Fact]
        public void A_stored_blob_is_written_as_the_reserved_key_and_nothing_else()
        {
            // Pinned as text. The shape is what earlier runs wrote, so a change to it is a change to
            // whether their values can still be read — and every byte here is copied at every hop
            // and into orchestration history, which is why absent fields are absent rather than
            // defaulted.
            Assert.Equal(
                """{"Value":{"$utos:blob":{"id":"b_1","size":400,"type":"text/plain"}},"Map":null}""",
                Write(WorkflowBlob.Stored("b_1", 400, "text/plain")));
        }

        [Fact]
        public void An_inline_blob_carries_its_bytes_as_base64()
        {
            Assert.Equal(
                """{"Value":{"$utos:blob":{"data":"aGk=","size":2,"type":"text/plain"}},"Map":null}""",
                Write(WorkflowBlob.Inline(Encoding.UTF8.GetBytes("hi"), "text/plain")));
        }

        [Fact]
        public void A_window_onto_a_stored_object_keeps_its_offset()
        {
            Assert.Equal(
                """{"Value":{"$utos:blob":{"id":"b_1","offset":4,"size":7,"type":"application/octet-stream"}},"Map":null}""",
                Write(WorkflowBlob.Stored("b_1", 7, offset: 4)));
        }

        [Fact]
        public void A_file_keeps_its_name_and_last_modified()
        {
            var blob = WorkflowBlob.Stored("b_1", 3, "image/png", name: "cat.png") with
            {
                LastModified = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            };

            Assert.Equal(
                """{"Value":{"$utos:blob":{"id":"b_1","size":3,"type":"image/png","name":"cat.png","lastModified":"2026-09-25T10:00:00+00:00"}},"Map":null}""",
                Write(blob));
        }
    }

    public class RoundTripping
    {
        [Fact]
        public void A_stored_blob_comes_back_a_blob()
        {
            var blob = Assert.IsType<WorkflowBlob>(
                RoundTrip(WorkflowBlob.Stored("b_1", 400, "text/plain", offset: 8, name: "a.txt")));

            Assert.Equal("b_1", blob.Id);
            Assert.Equal(8, blob.Offset);
            Assert.Equal(400, blob.Size);
            Assert.Equal("text/plain", blob.MediaType);
            Assert.Equal("a.txt", blob.Name);
            Assert.Null(blob.Data);
        }

        [Fact]
        public void An_inline_blob_comes_back_with_its_bytes()
        {
            var blob = Assert.IsType<WorkflowBlob>(
                RoundTrip(WorkflowBlob.Inline(Encoding.UTF8.GetBytes("hello"), "text/plain")));

            Assert.Equal("hello", Encoding.UTF8.GetString(blob.Data!));
            Assert.Equal(5, blob.Size);
        }

        [Fact]
        public void A_blob_nested_in_a_map_and_a_list_comes_back_a_blob()
        {
            // Where it actually travels: an activity's `input` is a map of whatever a template
            // rendered, and a fan-out's output is a list.
            var value = new Dictionary<string, object?>
            {
                ["photo"] = WorkflowBlob.Stored("b_1", 12),
                ["attachments"] = new List<object?> { WorkflowBlob.Stored("b_2", 34), "note" },
            };

            var read = Assert.IsAssignableFrom<IDictionary<string, object?>>(RoundTrip(value));

            Assert.Equal("b_1", Assert.IsType<WorkflowBlob>(read["photo"]).Id);

            var list = Assert.IsAssignableFrom<IReadOnlyList<object?>>(read["attachments"]);
            Assert.Equal("b_2", Assert.IsType<WorkflowBlob>(list[0]).Id);
            Assert.Equal("note", list[1]);
        }

        [Fact]
        public void A_declared_map_property_carries_a_blob_too()
        {
            // The `Dictionary<string, object>` path, which is the annotation nearly every Dapr
            // payload in the daemon carries. It used to hand its own keys to System.Text.Json,
            // which wrote them unescaped — so this asserts the escaping reaches the top level and
            // not only nested maps.
            var carrier = new Carrier(null, new Dictionary<string, object>
            {
                ["photo"] = WorkflowBlob.Stored("b_1", 12),
                ["$utos:blob"] = "data, not a handle",
            });

            var read = JsonSerializer.Deserialize<Carrier>(
                JsonSerializer.Serialize(carrier, WorkflowJson.Options), WorkflowJson.Options)!;

            Assert.Equal("b_1", Assert.IsType<WorkflowBlob>(read.Map!["photo"]).Id);
            Assert.Equal("data, not a handle", read.Map["$utos:blob"]);
        }
    }

    public class Escaping
    {
        [Theory]
        [InlineData("$utos:blob")]
        [InlineData("$utos:anything")]
        [InlineData("$utos:")]
        [InlineData("$$utos:blob")]
        [InlineData("$$$utos:blob")]
        public void A_key_the_encoding_could_claim_survives_as_data(string key)
        {
            // The property that makes the encoding unambiguous rather than merely unlikely. A
            // workflow may produce any map it likes, and the spec is explicit that no key in a value
            // is meaningful — so a document that emits this key must read it back unchanged, and must
            // not be able to forge a handle by writing one.
            var value = new Dictionary<string, object?>
            {
                [key] = new Dictionary<string, object?> { ["id"] = "b_forged", ["size"] = 99 },
            };

            var read = Assert.IsAssignableFrom<IDictionary<string, object?>>(RoundTrip(value));

            var inner = Assert.IsAssignableFrom<IDictionary<string, object?>>(
                Assert.Contains(key, read));

            Assert.Equal("b_forged", inner["id"]);
        }

        [Fact]
        public void An_ordinary_key_is_written_exactly_as_it_was()
        {
            // The cost of the escaping, bounded: a value with no at-risk key is byte-for-byte what
            // it was before blobs existed, so no event written by an earlier daemon reads differently.
            Assert.Equal(
                """{"Value":{"id":"x","$utos":1,"utos:blob":2,"$other:blob":3},"Map":null}""",
                Write(new Dictionary<string, object?>
                {
                    ["id"] = "x",
                    ["$utos"] = 1,
                    ["utos:blob"] = 2,
                    ["$other:blob"] = 3,
                }));
        }

        [Theory]
        [InlineData("$utos:blob", true)]
        [InlineData("$$utos:x", true)]
        [InlineData("$utos:", true)]
        [InlineData("$utos", false)]
        [InlineData("utos:blob", false)]
        [InlineData("$uto:blob", false)]
        [InlineData("blob", false)]
        [InlineData("$", false)]
        [InlineData("", false)]
        public void At_risk_is_any_run_of_dollars_before_the_prefix(string key, bool expected)
        {
            Assert.Equal(expected, WorkflowBlobJson.IsAtRisk(key));
        }

        [Theory]
        [InlineData("$utos:blob")]
        [InlineData("$$utos:blob")]
        [InlineData("plain")]
        [InlineData("$utos")]
        public void Escaping_is_reversible(string key)
        {
            Assert.Equal(key, WorkflowBlobJson.Unescape(WorkflowBlobJson.Escape(key)));
        }
    }

    public class WhatIsNotABlob
    {
        [Fact]
        public void The_reserved_key_beside_another_property_is_refused_rather_than_guessed()
        {
            // Only the daemon writes this key unescaped, and it never writes it beside anything. So
            // this is corrupt internal state, and reading it as either a blob or a map would be a
            // guess — one of which hands a workflow a handle it did not earn.
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Carrier>(
                """{"Value":{"$utos:blob":{"id":"b_1","size":1},"other":2}}""", WorkflowJson.Options));
        }

        [Fact]
        public void The_reserved_key_with_a_scalar_value_is_a_map()
        {
            // Not written by us either, but harmless and unambiguous: a blob's body is an object, so
            // a scalar here can only be data — from a `Dictionary<string, string>`, say, whose keys
            // this encoding deliberately leaves alone.
            var read = Assert.IsAssignableFrom<IDictionary<string, object?>>(
                JsonSerializer.Deserialize<Carrier>(
                    """{"Value":{"$utos:blob":"text"}}""", WorkflowJson.Options)!.Value);

            Assert.Equal("text", read["$utos:blob"]);
        }

        [Fact]
        public void An_unknown_field_in_a_blob_is_skipped_rather_than_refused()
        {
            // A run in flight across a deploy that added a field. The blob still resolves.
            var blob = Assert.IsType<WorkflowBlob>(JsonSerializer.Deserialize<Carrier>(
                """{"Value":{"$utos:blob":{"id":"b_1","size":1,"future":{"a":[1]}}}}""",
                WorkflowJson.Options)!.Value);

            Assert.Equal("b_1", blob.Id);
        }
    }
}
