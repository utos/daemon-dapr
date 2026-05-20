using Common.SeedWork;
using Dapr.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public class LogActivity(IWorkflowLogger logger) : WorkflowActivity<LogInput, object>
{
    public override async Task<object> RunAsync(WorkflowActivityContext context, LogInput input)
    {
        await logger.LogAsync(input.ExecutionId, input.Source, input.Category, input.Level, input.Message);
        return new object();
    }
}

public sealed record LogInput(
    Guid ExecutionId,
    string Source,
    string? Category,
    WorkflowLogLevel Level,
    string Message);
