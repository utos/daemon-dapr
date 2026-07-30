using Execution.Features.Models;

namespace Execution.Features.Repositories;

/// <summary>
/// Repository for querying workflow execution read models
/// </summary>
public interface IWorkflowExecutionRepository
{
    /// <summary>
    /// Get detailed execution information by ID
    /// </summary>
    Task<ExecutionDetails?> GetExecutionDetailsAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// List all executions ordered by creation date (newest first)
    /// </summary>
    Task<List<ExecutionSummary>> ListExecutionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determine whether an execution may be deleted, based on its terminal status.
    /// </summary>
    Task<ExecutionDeletionEligibility> GetDeletionEligibilityAsync(Guid executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the ids of all non-detached sub-workflow executions parented to the given execution.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetChildExecutionIdsAsync(Guid parentExecutionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the read-model rows for the given execution ids.
    /// </summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> executionIds, CancellationToken cancellationToken = default);
}
