namespace Common.Workflow;

/// <summary>
/// Binary data as a run holds it: a seventh kind of value, alongside null, bool, number, string,
/// list and map.
/// <para>
/// A type rather than a shape. Spec 0.20 made a value's kind structural precisely so that no key
/// inside the data — <c>$blob</c> or any other — decides what something is, which means a response
/// body, a client's input and a child's result can all contain the key <c>$blob</c> and it stays
/// an ordinary map. See <c>api/docs/binary-data.md</c>.
/// </para>
/// <para>
/// Two backings, and exactly one is set. <see cref="Data"/> is the bytes themselves, carried in
/// the value; <see cref="Id"/> names an object the daemon has stored, and
/// <see cref="Offset"/>/<see cref="Size"/> are a window onto it, so a slice of a large object
/// costs a handle rather than a copy.
/// </para>
/// </summary>
/// <param name="Offset">
/// Where this blob starts within the stored object. Always 0 for an inline blob: it carries its
/// own bytes, so there is nothing to be offset from.
/// </param>
/// <param name="Size">
/// How many bytes this blob is. For an inline blob it equals the length of <see cref="Data"/> —
/// an invariant, not a convenience, and one <c>UTOS-F104</c> reports when it does not hold.
/// </param>
/// <param name="MediaType">
/// What the bytes are, as an IANA media type. Never inferred from the bytes and never from a file
/// extension: the producer declares it, and <c>application/octet-stream</c> is the honest answer
/// where nothing better is known.
/// </param>
public sealed record WorkflowBlob(
    string? Id = null,
    byte[]? Data = null,
    long Offset = 0,
    long Size = 0,
    string MediaType = "application/octet-stream",
    string? Name = null,
    DateTimeOffset? LastModified = null)
{
    /// <summary>True when the bytes travel in the value rather than in the daemon's store.</summary>
    public bool IsInline => Data is not null;

    public static WorkflowBlob Inline(
        byte[] data, string mediaType = "application/octet-stream", string? name = null) =>
        new(Data: data, Size: data.Length, MediaType: mediaType, Name: name);

    public static WorkflowBlob Stored(
        string id, long size, string mediaType = "application/octet-stream",
        long offset = 0, string? name = null) =>
        new(id, Offset: offset, Size: size, MediaType: mediaType, Name: name);
}
