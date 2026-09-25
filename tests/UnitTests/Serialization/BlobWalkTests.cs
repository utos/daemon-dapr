using Common.Workflow;

namespace UnitTests.Serialization;

/// <summary>
/// Finding the blobs in a run value, and where each one is.
/// <para>
/// Two callers, opposite directions. Promotion walks a root execution's output to learn which blobs
/// somebody asked for; <c>ScheduleExecution</c> walks a client's input to check each one and reports
/// a failure per blob whose <c>field</c> is a JSON Pointer. Pointers are the reason the walk is
/// ordered and the reason its escaping is a rule rather than a nicety.
/// </para>
/// </summary>
public class BlobWalkTests
{
    private static WorkflowBlob Blob(string id) => WorkflowBlob.Stored(id, 1);

    [Fact]
    public void A_blob_at_the_root_is_found_at_the_empty_pointer()
    {
        // The whole document is the blob. "" is the JSON Pointer for it, not "/".
        Assert.Equal([("", "b_1")], Located(Blob("b_1")));
    }

    [Fact]
    public void Blobs_are_found_through_maps_and_lists_in_document_order()
    {
        var value = new Dictionary<string, object?>
        {
            ["photo"] = Blob("b_1"),
            ["meta"] = new Dictionary<string, object?>
            {
                ["thumb"] = Blob("b_2"),
                ["width"] = 640,
            },
            ["attachments"] = new List<object?> { "note", Blob("b_3"), null },
        };

        Assert.Equal(
            [("/photo", "b_1"), ("/meta/thumb", "b_2"), ("/attachments/1", "b_3")],
            Located(value));
    }

    [Fact]
    public void A_value_with_no_blobs_is_walked_without_finding_any()
    {
        Assert.Empty(BlobWalk.In(new Dictionary<string, object?>
        {
            ["a"] = "text",
            ["b"] = new List<object?> { 1, 2.5, true, null },
            ["c"] = new Dictionary<string, object?>(),
        }));
    }

    [Fact]
    public void A_string_is_not_walked_as_a_sequence()
    {
        // It is an IEnumerable, and walking one as a sequence would recurse once per character. The
        // walk matches the two shapes the value converter produces and nothing else.
        Assert.Empty(BlobWalk.In("a string"));
        Assert.Empty(BlobWalk.In(42));
        Assert.Empty(BlobWalk.In(null));
    }

    [Fact]
    public void A_key_with_a_slash_or_a_tilde_is_escaped_so_the_pointer_still_points()
    {
        // RFC 6901, and in this order: `~` becomes `~0` first, then `/` becomes `~1`. The other order
        // turns a literal `~1` into `~01` and the pointer resolves to the wrong place — or to nothing.
        var value = new Dictionary<string, object?>
        {
            ["a/b"] = Blob("b_1"),
            ["c~d"] = Blob("b_2"),
            ["e~1f"] = Blob("b_3"),
        };

        Assert.Equal(
            [("/a~1b", "b_1"), ("/c~0d", "b_2"), ("/e~01f", "b_3")],
            Located(value));
    }

    [Fact]
    public void The_same_blob_named_twice_is_found_twice()
    {
        // Two places, two pointers — a report has to name both. Promotion deduplicates by id, which
        // is its business and not the walk's.
        Assert.Equal(
            [("/a", "b_1"), ("/b", "b_1")],
            Located(new Dictionary<string, object?> { ["a"] = Blob("b_1"), ["b"] = Blob("b_1") }));
    }

    [Fact]
    public void A_caller_walking_one_field_can_say_what_the_pointers_are_relative_to()
    {
        // `ScheduleExecution` walks the input map, whose pointers are relative to `/input`.
        Assert.Equal(
            [("/input/photo", "b_1")],
            Located(new Dictionary<string, object?> { ["photo"] = Blob("b_1") }, "/input"));
    }

    private static (string, string?)[] Located(object? value, string root = "") =>
        BlobWalk.Locate(value, root).Select(found => (found.Pointer, found.Blob.Id)).ToArray();
}
