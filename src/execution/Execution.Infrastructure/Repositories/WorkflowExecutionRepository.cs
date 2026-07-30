using Microsoft.EntityFrameworkCore;
using Execution.Infrastructure.Extensions;
using Execution.Features.Models;
using Execution.Features.Repositories;
using Execution.Infrastructure.Data;
using Execution.Infrastructure.ReadModels;

namespace Execution.Infrastructure.Repositories;

/// <summary>
/// Repository implementation for querying workflow execution read models
/// </summary>
public class WorkflowExecutionRepository(WorkflowExecutionDbContext dbContext) : IWorkflowExecutionRepository
{
    public async Task<ExecutionDetails?> GetExecutionDetailsAsync(Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var readModel = await dbContext.WorkflowExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

        return readModel?.ToDetailsDto();
    }

    public async Task<List<ExecutionSummary>> ListExecutionsAsync(CancellationToken cancellationToken = default)
    {
        var readModels = await dbContext.WorkflowExecutions
            .AsNoTracking()
            .Where(e => e.ParentExecutionId == null)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        return readModels.Select(rm => rm.ToSummaryDto()).ToList();
    }

    public async Task<ExecutionDeletionEligibility> GetDeletionEligibilityAsync(Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var status = await dbContext.WorkflowExecutions
            .AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => e.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (status is null)
            return ExecutionDeletionEligibility.NotFound;

        return status is ReadModelStatus.Completed or ReadModelStatus.Failed
            ? ExecutionDeletionEligibility.Terminal
            : ExecutionDeletionEligibility.NonTerminal;
    }

    public async Task<IReadOnlyList<Guid>> GetChildExecutionIdsAsync(Guid parentExecutionId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkflowExecutions
            .AsNoTracking()
            .Where(e => e.ParentExecutionId == parentExecutionId)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> executionIds,
        CancellationToken cancellationToken = default)
    {
        if (executionIds.Count == 0)
            return;

        await dbContext.WorkflowExecutions
            .Where(e => executionIds.Contains(e.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
