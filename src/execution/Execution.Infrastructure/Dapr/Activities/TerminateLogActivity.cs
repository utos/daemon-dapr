using Common.SeedWork;
using Dapr.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public class TerminateLogActivity(IWorkflowLogger logger) : WorkflowActivity<TerminateLogInput, object>
{
    public override async Task<object> RunAsync(WorkflowActivityContext context, TerminateLogInput input)
    {
        await logger.TerminateAsync(input.ExecutionId);
        return new object();
    }
}

public sealed record TerminateLogInput(Guid ExecutionId);
