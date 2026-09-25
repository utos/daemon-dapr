using Common.SeedWork;

namespace Common.Blobs;

/// <summary>
/// Removing bytes nobody can reach any more — <c>api/docs/binary-data.md</c> § Retention.
/// <para>
/// A second contract beside <see cref="IBlobAccess"/> rather than more methods on it, because the
/// two have different callers and different reasons to exist. <see cref="IBlobAccess"/> is what a
/// <em>run</em> does with blobs; this is what an <em>operator</em> does, reached from a background
/// sweep and from <c>DeleteExecution</c>. Nothing a workflow can say gets here.
/// </para>
/// </summary>
public interface IBlobReclamation
{
    /// <summary>
    /// The periodic sweep. It catches what the two accurate signals miss: uploads created but never
    /// completed, the intermediates of runs interrupted before they reached a terminal state, and
    /// anything past the failed-run window.
    /// </summary>
    Task<BlobReclamationReport> SweepAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>DeleteExecution</c>: the tree's intermediates go at once, and with
    /// <paramref name="deleteDurable"/> so do the durable blobs it produced.
    /// <para>
    /// Never a client's upload, which may have started other runs and is deleted with
    /// <c>DeleteBlob</c>. And never a blob a non-terminal execution can still reference — a durable
    /// upload attached to this tree and to a live one is skipped, not deleted.
    /// </para>
    /// </summary>
    Task<BlobReclamationReport> ReclaimRunTreeAsync(
        ExecutionId rootExecution, bool deleteDurable, CancellationToken cancellationToken);
}

/// <summary>
/// What one pass removed. Returned rather than only logged so that a sweep can be asserted on, and
/// so that a caller running the sweep on demand can say what it did.
/// </summary>
/// <param name="Deleted">Blobs whose bytes are gone and whose record now reads <c>DELETED</c>.</param>
/// <param name="Skipped">
/// Blobs a pass considered and left alone — a run tree that has not ended, a durable blob a caller
/// did not ask to delete. Counted because "nothing was deleted" and "there was nothing to delete"
/// are different answers and an operator has to be able to tell them apart.
/// </param>
/// <param name="Failed">
/// Blobs whose bytes the store would not delete. The record is left alone, so the next pass tries
/// again — the opposite order would leave an object nothing remembers, which is the one outcome
/// nobody can clean up.
/// </param>
public sealed record BlobReclamationReport(int Deleted, int Skipped, int Failed)
{
    public static BlobReclamationReport Empty { get; } = new(0, 0, 0);

    public bool DidNothing => Deleted == 0 && Skipped == 0 && Failed == 0;

    public BlobReclamationReport Plus(BlobReclamationReport other) =>
        new(Deleted + other.Deleted, Skipped + other.Skipped, Failed + other.Failed);
}
