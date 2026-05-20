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

    public async Task<ExecutionContext> LoadContextAsync(Guid executionId, CancellationToken ct = default)
    {
        return await db.ExecutionContexts.FindAsync([executionId], ct)
               ?? throw new InvalidOperationException($"Execution context not found for {executionId}");
    }

    public async Task DeleteAllAsync(Guid executionId, CancellationToken ct = default)
    {
        var context = await db.ExecutionContexts.FindAsync([executionId], ct);
        if (context != null)
            db.ExecutionContexts.Remove(context);

        await db.SaveChangesAsync(ct);
    }
}
