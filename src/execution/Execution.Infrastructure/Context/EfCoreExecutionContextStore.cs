using Common.SeedWork;
using Microsoft.EntityFrameworkCore;
using Execution.Domain.Services;
using Execution.Infrastructure.Data;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace Execution.Infrastructure.Context;

// TODO: Consider switching to Dapr State Store (DaprClient.SaveStateAsync/GetStateAsync
// with ConsistencyMode.Strong) for better alignment with the Dapr ecosystem.

public class EfCoreExecutionContextStore(WorkflowExecutionDbContext db) : IExecutionContextStore
{
    public async Task SaveContextAsync(ExecutionContext context, CancellationToken ct = default)
    {
        var existing = await db.ExecutionContexts.FindAsync([context.ExecutionId], ct);
        if (existing != null)
            return;

        db.ExecutionContexts.Add(context);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ExecutionContext> LoadContextAsync(ExecutionId executionId, CancellationToken ct = default)
    {
        return await db.ExecutionContexts.FindAsync([executionId], ct)
               ?? throw new InvalidOperationException($"Execution context not found for {executionId}");
    }

    public async Task<IReadOnlyList<ExecutionId>> ListDescendantsAsync(
        ExecutionId rootExecutionId, CancellationToken ct = default)
    {
        return await db.ExecutionContexts
            .Where(context => context.RootExecutionId == rootExecutionId)
            .Select(context => context.ExecutionId)
            .ToListAsync(ct);
    }

    public async Task DeleteAllAsync(ExecutionId rootExecutionId, CancellationToken ct = default)
    {
        // The run's own row and every descendant's, in one statement. This used to delete a
        // single row despite its name, which is how a sub-workflow's context outlived the run
        // that created it with nothing left pointing at it.
        await db.ExecutionContexts
            .Where(context => context.ExecutionId == rootExecutionId
                              || context.RootExecutionId == rootExecutionId)
            .ExecuteDeleteAsync(ct);
    }
}
