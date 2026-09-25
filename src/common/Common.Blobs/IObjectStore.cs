namespace Common.Blobs;

/// <summary>
/// Where a stored blob's bytes actually live — <c>api/docs/binary-data.md</c> § Storage.
/// <para>
/// Four requirements, and they are why this is object storage rather than a disk. The objects are
/// <b>shared</b>, so a blob produced on one node is readable by an activity scheduled on another.
/// They are <b>immutable and read-after-write</b>, which is what lets two executions read one blob
/// and never disagree, and lets a replay read what the first run read. The store can issue
/// <b>signed locations</b>, so a client moves bytes without the daemon relaying them and without
/// holding store credentials — a daemon never signs anything itself. And reads and writes
/// <b>stream</b>, so neither a spilled response body nor a streamed request body is ever held in
/// memory.
/// </para>
/// <para>
/// A port, not a base class, and deliberately spoken in terms of keys and streams rather than
/// blobs: what a blob <em>is</em> — its id, its class, its history, which run trees may read it —
/// is the daemon's record, and this knows none of it. An <see cref="ObjectKey"/> is an address in
/// a bucket; a blob id is not (§ A blob as a value), and the mapping between them is the daemon's
/// business.
/// </para>
/// <para>
/// A daemon may have no store at all. That is a supported configuration, not a broken one: inline
/// blobs still work, and anything that would have to be stored fails with <c>UTOS-F102</c> while
/// <c>BlobService</c> answers <c>FAILED_PRECONDITION</c>. So this is resolved as a nullable
/// dependency, and "is there a store?" is a question the callers ask rather than one they assume.
/// </para>
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="key"/>, streaming, and returns what the
    /// store now holds. The caller does not know the length in advance — a chunked response has no
    /// trustworthy <c>content-length</c> — so the count of bytes actually written is the answer.
    /// </summary>
    Task<ObjectStat> WriteAsync(
        ObjectKey key, Stream content, string mediaType, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a window onto an object: <paramref name="length"/> bytes from
    /// <paramref name="offset"/>. That window is what makes slicing a stored blob free — a slice is
    /// the same object at a different offset and size, and copies nothing.
    /// </summary>
    Task<Stream> OpenReadAsync(
        ObjectKey key, long offset, long length, CancellationToken cancellationToken);

    /// <summary>
    /// What the store holds at <paramref name="key"/>, or null when it holds nothing. This is how
    /// <c>CompleteBlob</c> learns the real size of what a client uploaded, rather than trusting
    /// the size the client declared.
    /// </summary>
    Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the object. Removing one that is not there succeeds: retention sweeps run more than
    /// once over the same wreckage, and a sweep that fails on an object a previous sweep already
    /// took is a sweep that never finishes.
    /// </summary>
    Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// A location a client may <c>PUT</c> bytes to, valid for <paramref name="expiry"/>. The bytes
    /// go client-to-store; the daemon is not in the path and never sees them.
    /// </summary>
    Task<SignedLocation> SignUploadAsync(
        ObjectKey key, string mediaType, TimeSpan expiry, CancellationToken cancellationToken);

    /// <summary>
    /// A location a client may <c>GET</c> bytes from, valid for <paramref name="expiry"/>. A
    /// handle that covers part of an object is downloaded as a range, so the caller asks for the
    /// range rather than the whole object.
    /// </summary>
    Task<SignedLocation> SignDownloadAsync(
        ObjectKey key, ByteRange? range, TimeSpan expiry, CancellationToken cancellationToken);
}

/// <summary>
/// An address in the store. Not a blob id and never derived from one in a way a client could
/// invert: an id is resolved by looking it up in the daemon's records, which is what makes
/// forging one useless (§ Access).
/// </summary>
public readonly record struct ObjectKey(string Value)
{
    public override string ToString() => Value;
}

/// <summary>What the store holds at a key: how many bytes, and what it calls them.</summary>
public sealed record ObjectStat(long Size, string MediaType, string? ETag = null);

/// <summary>A half-open byte range, as a stored blob's <c>offset</c> and <c>size</c> describe one.</summary>
public readonly record struct ByteRange(long Offset, long Length);

/// <summary>
/// A pre-authorized URL and the headers that must accompany it, as
/// <c>utos.daemon.v1.SignedLocation</c> carries to a client.
/// <para>
/// The headers are not advisory. A store may sign a request that includes them, in which case
/// sending the URL without them is rejected — so a client replays exactly what it was given.
/// </para>
/// </summary>
/// <param name="Method">
/// The HTTP method the location was signed for — <c>PUT</c> for an upload, <c>GET</c> for a download.
/// Part of the signature and not a hint: a store signs the method along with the URL, so replaying the
/// URL with a different one is refused.
/// </param>
public sealed record SignedLocation(
    string Url,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt)
{
    public static SignedLocation At(string url, string method, DateTimeOffset expiresAt) =>
        new(url, method, new Dictionary<string, string>(), expiresAt);
}

/// <summary>
/// The store could not do what was asked. Distinguishes a <see cref="Transient"/> failure — a
/// timeout, throttling — from a permanent one, because the spec draws that line: a transient
/// failure is retried as any activity-level I/O is, and only reported as <c>UTOS-F103</c> once the
/// implementation gives up.
/// </summary>
public sealed class ObjectStoreException(string message, bool transient, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}
