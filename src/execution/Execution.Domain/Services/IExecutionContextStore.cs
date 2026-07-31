using Common.SeedWork;
namespace Execution.Domain.Services;

public interface IExecutionContextStore
{
    Task SaveContextAsync(ExecutionContext context, CancellationToken ct = default);
    Task<ExecutionContext> LoadContextAsync(ExecutionId executionId, CancellationToken ct = default);

    Task DeleteAllAsync(ExecutionId executionId, CancellationToken ct = default);
}
