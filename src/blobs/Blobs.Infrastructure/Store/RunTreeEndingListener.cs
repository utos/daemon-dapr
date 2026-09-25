using Blobs.Domain;
using Common.ExecutionLifecycle;
using Common.SeedWork;

namespace Blobs.Infrastructure.Store;

/// <summary>
/// How the blobs module learns that a run tree is finished, without knowing what an execution
/// aggregate is or where its events live.
/// <para>
/// This is what makes the sweep accurate rather than merely eventual. Without it the only thing the
/// sweep could go on is the age of an attachment, and an intermediate nobody will ever read again
/// would sit in the bucket until that backstop expired weeks later.
/// </para>
/// <para>
/// One <c>UPDATE</c> over the tree's attachments and nothing else. The contract's remark is explicit
/// that this is called inside the subscription's own await chain — a listener that throws parks the
/// event and one that blocks stalls the subscription — so deleting bytes here would put an object
/// store in the path of every terminating run. The deleting is the sweep's job.
/// </para>
/// <para>
/// Fires for every execution, and only a <em>root</em> has attachments, so a child's ending matches
/// no row and costs one indexed statement that updates nothing. Filtering it out here would mean
/// asking the execution module which executions are roots, which is the edge this contract exists to
/// avoid.
/// </para>
/// </summary>
public sealed class RunTreeEndingListener(IBlobRepository repository) : IExecutionLifecycleListener
{
    public Task OnCompletedAsync(ExecutionId executionId, DateTime occurredAt, CancellationToken ct = default) =>
        repository.RecordRunTreeEndedAsync(
            executionId, RunTreeOutcome.Completed, At(occurredAt), ct);

    public Task OnFailedAsync(
        ExecutionId executionId, DateTime occurredAt, string reason, string? code = null,
        CancellationToken ct = default) =>
        repository.RecordRunTreeEndedAsync(
            executionId, RunTreeOutcome.Failed, At(occurredAt), ct);

    public Task OnCancelledAsync(
        ExecutionId executionId, DateTime occurredAt, string? reason, CancellationToken ct = default) =>
        repository.RecordRunTreeEndedAsync(
            executionId, RunTreeOutcome.Cancelled, At(occurredAt), ct);

    /// <summary>
    /// The events carry a <see cref="DateTime"/> written as UTC, and the retention windows are
    /// measured against a <see cref="DateTimeOffset"/>. Said once, because reading it as local time
    /// would shift every window by the deployment's offset and nothing would look wrong.
    /// </summary>
    private static DateTimeOffset At(DateTime occurredAt) =>
        new(DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc));
}
