using Microsoft.EntityFrameworkCore;
using Execution.Infrastructure.Extensions;
using Execution.Features.Models;
using Execution.Features.Repositories;
using Execution.Infrastructure.Data;

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
}
