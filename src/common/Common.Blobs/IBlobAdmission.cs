using Common.SeedWork;
using Common.Workflow;

namespace Common.Blobs;

/// <summary>
/// Letting a blob into a run — <c>api/docs/binary-data.md</c> § Into a run and § Access.
/// <para>
/// A third contract beside <see cref="IBlobAccess"/> and <see cref="IBlobReclamation"/> because it
/// has a third caller. <see cref="IBlobAccess"/> is what a run does with blobs it already holds;
/// <see cref="IBlobReclamation"/> is what an operator does; this is the boundary, and it is the only
/// one of the three whose answer is a <em>list of refusals</em> rather than a result — because a
/// request naming five bad handles has to be told about all five.
/// </para>
/// </summary>
public interface IBlobAdmission
{
    /// <summary>
    /// A client's blobs at <c>ScheduleExecution</c>. Each must be <b>durable</b>: an intermediate
    /// belongs to another run and was not carried out of it, so attaching it would keep alive bytes
    /// the daemon is entitled to remove.
    /// <para>
    /// A client needs no relationship to the blob. The spec defines no per-caller authorization for
    /// blobs, just as it defines none for executions — every caller a daemon admits may attach any
    /// durable blob, and restricting that is a deployment's concern.
    /// </para>
    /// </summary>
    /// <param name="arrival">
    /// The run being started, as the history row will record it: <c>RootExecution</c> is the new run
    /// tree, and the workflow and digest name what it is about to run.
    /// </param>
    /// <returns>
    /// One entry per failing blob, empty when every blob was admitted and attached. Empty is the
    /// only answer that means anything happened — attaching is done here, so a caller that ignores a
    /// non-empty list has scheduled a run that cannot read its own input.
    /// </returns>
    Task<IReadOnlyList<BlobAdmissionFailure>> AdmitFromClientAsync(
        object? input, BlobUsage arrival, CancellationToken cancellationToken);

    /// <summary>
    /// A <c>workflow.spawn</c>'s blobs. A spawn starts a <b>new</b> run tree, so what the spawned run
    /// receives has to be attached to it — and each blob must already belong to
    /// <paramref name="spawningTree"/>, after which it belongs to both.
    /// <para>
    /// Durability is <em>not</em> required here, unlike a client's attach: an intermediate the
    /// spawning tree produced is a value the parent legitimately holds, and carrying it into a run it
    /// starts is passing on a value rather than reaching for somebody else's object.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BlobAdmissionFailure>> AdmitFromSpawnAsync(
        object? input, BlobUsage arrival, ExecutionId spawningTree, CancellationToken cancellationToken);
}

/// <summary>
/// Why one blob was not admitted. <c>Pointer</c> is its JSON Pointer into the input, which is what a
/// <c>google.rpc.BadRequest</c> field violation carries, so a client is told which blob rather than
/// that one of them was wrong.
/// </summary>
public sealed record BlobAdmissionFailure(string Pointer, BlobAdmissionReason Reason)
{
    public override string ToString() =>
        $"{(Pointer.Length == 0 ? "(root)" : Pointer)}: {Reason.Token()}";
}

/// <summary>
/// The reasons § Into a run names. The <see cref="BlobAdmissionReasons.Token">token</see> is the
/// contract a client branches on; this enum is how the daemon spells it.
/// </summary>
public enum BlobAdmissionReason
{
    /// <summary>No blob has this id — or, at a spawn, none this run tree can see.</summary>
    NotFound,

    /// <summary>The blob is <c>PENDING</c>: its upload was never completed.</summary>
    NotReady,

    /// <summary>The blob is <c>DELETED</c>.</summary>
    Deleted,

    /// <summary>
    /// It belongs to another run and was not carried out of it. Only a durable blob may be attached
    /// by a client.
    /// </summary>
    Intermediate,

    /// <summary><c>offset + size</c> exceeds the stored object's length.</summary>
    OutOfRange,

    /// <summary>An invariant of § A blob as a value does not hold.</summary>
    Malformed,

    /// <summary>An inline blob exceeds the inline threshold. Upload it instead.</summary>
    TooLarge,
}

public static class BlobAdmissionReasons
{
    /// <summary>
    /// The spec's token, which is what travels as a field violation's <c>reason</c>. Written out
    /// rather than derived from the enum name: the token is the contract and the member name is not,
    /// so a rename must not be able to change it.
    /// </summary>
    public static string Token(this BlobAdmissionReason reason) => reason switch
    {
        BlobAdmissionReason.NotFound => "not_found",
        BlobAdmissionReason.NotReady => "not_ready",
        BlobAdmissionReason.Deleted => "deleted",
        BlobAdmissionReason.Intermediate => "intermediate",
        BlobAdmissionReason.OutOfRange => "out_of_range",
        BlobAdmissionReason.Malformed => "malformed",
        BlobAdmissionReason.TooLarge => "too_large",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown admission reason"),
    };
}

/// <summary>
/// A run's input named blobs that cannot be attached — <c>UTOS-F104</c>.
/// <para>
/// One exception carrying every failure, never the first: fixing one problem must not reveal the
/// next. At <c>ScheduleExecution</c> the transport turns this into <c>INVALID_ARGUMENT</c> with a
/// field violation per blob; at a <c>workflow.spawn</c> it fails the spawn activity, where the code
/// is what an <c>onFailure</c> rule matches on.
/// </para>
/// </summary>
public sealed class BlobsNotAdmittedException(IReadOnlyList<BlobAdmissionFailure> failures)
    : Exception(Describe(failures))
{
    public const string Code = "UTOS-F104";

    public IReadOnlyList<BlobAdmissionFailure> Failures { get; } = failures;

    /// <summary>
    /// The code leads the message, because that is the only part of it that survives a Dapr activity
    /// boundary — where the exception is flattened to its text and a spawn's failure code has to be
    /// recovered from what is left.
    /// </summary>
    private static string Describe(IReadOnlyList<BlobAdmissionFailure> failures) =>
        $"{Code}: a blob in the run's input cannot be attached: "
        + string.Join("; ", failures.Select(failure => failure.ToString()));
}
