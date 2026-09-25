using Common.SeedWork;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Deletes a terminal execution's record — its read-model row and event streams — together
/// with the records of its non-detached sub-workflow executions. Detached sub-workflows are
/// independent top-level executions and are not affected.
/// </summary>
/// <param name="DeleteBlobs">
/// Also delete the durable blobs this run tree produced — what it returned or emitted. The tree's
/// intermediates go either way: nobody asked for them, and the execution that could have referenced
/// them is being removed.
/// <para>
/// Never a client's upload, which may have started other runs and is deleted with
/// <c>BlobService.DeleteBlob</c>. And never a blob a non-terminal execution can still reference,
/// which is skipped rather than deleted.
/// </para>
/// </param>
public record DeleteExecutionCommand(ExecutionId ExecutionId, bool DeleteBlobs = false)
    : ICommand<DeleteExecutionResult>;

public record DeleteExecutionResult;
