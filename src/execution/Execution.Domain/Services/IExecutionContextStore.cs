namespace Execution.Domain.Services;

public interface IExecutionContextStore
{
    Task SaveContextAsync(ExecutionContext context, CancellationToken ct = default);
    Task<ExecutionContext> LoadContextAsync(Guid executionId, CancellationToken ct = default);

    Task DeleteAllAsync(Guid executionId, CancellationToken ct = default);
}
