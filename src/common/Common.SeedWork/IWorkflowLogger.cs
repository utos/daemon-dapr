namespace Common.SeedWork;

public interface IWorkflowLogger
{
    Task LogAsync(Guid executionId, string source, string? category,
        WorkflowLogLevel level, string message);

    Task TerminateAsync(Guid executionId);
}
