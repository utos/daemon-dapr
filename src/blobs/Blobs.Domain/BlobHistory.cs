using Common.SeedWork;

namespace Blobs.Domain;

/// <summary>What happened to a blob. <c>utos.daemon.v1.BlobEventKind</c>.</summary>
public enum BlobEventKind
{
    /// <summary>A client created it with <c>CreateBlob</c>.</summary>
    Uploaded,

    /// <summary>An activity wrote it: a response body, or a blob an expression built.</summary>
    Produced,

    /// <summary>It arrived as a run tree's input, at <c>ScheduleExecution</c> or a <c>workflow.spawn</c>.</summary>
    Attached,

    /// <summary>
    /// An execution opened its bytes — streamed them into a request, or materialized them in an
    /// expression. Slicing is not a read, and neither is passing the value on.
    /// </summary>
    Read,

    /// <summary>A client was issued a download location.</summary>
    Downloaded,
}

/// <summary>
/// One row of a blob's timeline, as <c>GetBlobHistory</c> returns it.
/// <para>
/// <b>Rows are aggregated by (kind, root execution, workflow).</b> Recording an event that already
/// has a row moves <see cref="LastAt"/> and adds to <see cref="Executions"/> rather than adding a
/// row. So a loop reading a blob five thousand times in one execution is one row, and so is a loop
/// calling five thousand sub-workflows that each read it. The number of rows is bounded by how many
/// distinct documents touched the blob — small and predictable — rather than by fan-out, which is
/// neither.
/// </para>
/// <para>
/// What that gives up is which individual child execution read the blob. Nothing needs it. Events
/// are about the handle, not the bytes: "this workflow used this blob" is worth keeping, "this
/// activity read bytes 50 to 60" is not, and would grow without bound.
/// </para>
/// </summary>
/// <param name="RootExecution">
/// The root of the run tree, not the execution that did the reading — that is what makes fan-out
/// collapse to one row. Null for <see cref="BlobEventKind.Uploaded"/> and
/// <see cref="BlobEventKind.Downloaded"/>, which involve no execution and are one row each.
/// </param>
/// <param name="WorkflowDigest">
/// The bundle's content digest, recorded beside its name. A name and version stop identifying what
/// ran once a bundle is rebuilt; the digest does not, and it is already computed at load.
/// </param>
/// <param name="Executions">
/// How many distinct executions this row stands for. One for a row that a single run produced; more
/// once fan-out has collapsed into it.
/// </param>
public sealed record BlobHistoryEntry(
    BlobEventKind Kind,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    ExecutionId? RootExecution = null,
    string? Workflow = null,
    string? WorkflowDigest = null,
    string? Activity = null,
    int Executions = 1)
{
    /// <summary>
    /// What two rows must share to be the same row. Deliberately not the activity: a document that
    /// reads a blob from three activities is one row, because the question a history answers is
    /// which workflows used a blob, and naming one of the activities is enough to find it.
    /// </summary>
    public (BlobEventKind, ExecutionId?, string?) Key => (Kind, RootExecution, Workflow);

    /// <summary>Folds another occurrence of the same event into this row.</summary>
    public BlobHistoryEntry Merged(DateTimeOffset at, bool newExecution) =>
        this with
        {
            LastAt = at > LastAt ? at : LastAt,
            FirstAt = at < FirstAt ? at : FirstAt,
            Executions = newExecution ? Executions + 1 : Executions,
        };
}
