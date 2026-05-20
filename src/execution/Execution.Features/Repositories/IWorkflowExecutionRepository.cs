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
}
