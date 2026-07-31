using Common.SeedWork;
using Execution.Application.Models;
using Common.Workflow;

namespace Execution.Application.Repositories;

/// <summary>
/// Repository for querying workflow execution read models
/// </summary>
public interface IWorkflowExecutionRepository
{
    /// <summary>
    /// Get detailed execution information by ID
    /// </summary>
    Task<ExecutionDetails?> GetExecutionDetailsAsync(ExecutionId executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// List top-level executions ordered by creation date (newest first). When
    /// <paramref name="workflow"/> is non-null, results are filtered to that workflow:
    /// name is matched exactly and each other populated field (version, namespace,
    /// registry, digest) narrows further; an empty/unset field imposes no constraint.
    /// </summary>
    Task<List<ExecutionSummary>> ListExecutionsAsync(WorkflowReference? workflow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determine whether an execution may be deleted, based on its terminal status.
    /// </summary>
    Task<ExecutionDeletionEligibility> GetDeletionEligibilityAsync(ExecutionId executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the read-model rows for the given execution ids.
    /// </summary>
    Task DeleteAsync(IReadOnlyCollection<ExecutionId> executionIds, CancellationToken cancellationToken = default);
}
