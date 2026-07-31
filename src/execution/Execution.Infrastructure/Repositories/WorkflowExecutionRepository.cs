using Microsoft.EntityFrameworkCore;
using Execution.Infrastructure.Extensions;
using Execution.Features.Models;
using Execution.Features.Repositories;
using Execution.Infrastructure.Data;
using Execution.Infrastructure.ReadModels;
using Common.Workflow;

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

    public async Task<List<ExecutionSummary>> ListExecutionsAsync(WorkflowReference? workflow,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.WorkflowExecutions
            .AsNoTracking()
            .Where(e => e.ParentExecutionId == null);

        if (workflow is not null)
        {
            // name selects the workflow; each other populated field narrows further.
            // An unset field imposes no constraint — an omitted version matches all of them.
            query = query.Where(e => e.WorkflowName == workflow.Name);

            if (!string.IsNullOrEmpty(workflow.Version))
                query = query.Where(e => e.WorkflowVersion == workflow.Version);

            if (!string.IsNullOrEmpty(workflow.Namespace))
                query = query.Where(e => e.WorkflowNamespace == workflow.Namespace);

            if (!string.IsNullOrEmpty(workflow.Registry))
                query = query.Where(e => e.WorkflowRegistry == workflow.Registry);

            if (!string.IsNullOrEmpty(workflow.Digest))
                query = query.Where(e => e.WorkflowDigest == workflow.Digest);
        }

        var readModels = await query
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
