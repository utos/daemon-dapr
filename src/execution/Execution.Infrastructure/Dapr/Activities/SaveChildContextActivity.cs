using Dapr.Workflow;
using Execution.Domain.Services;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record SaveChildContextInput(
    Guid ParentExecutionId,
    Guid ChildExecutionId,
    string WorkflowKey,
    Dictionary<string, object>? Input);

public class SaveChildContextActivity(IExecutionContextStore contextStore)
    : WorkflowActivity<SaveChildContextInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, SaveChildContextInput input)
    {
        var parentContext = await contextStore.LoadContextAsync(input.ParentExecutionId);

        var childContext = new ExecutionContext
        {
            ExecutionId = input.ChildExecutionId,
            Bundle = parentContext.Bundle,
            EntryPoint = input.WorkflowKey,
            Input = input.Input ?? new Dictionary<string, object>(),
            // A sub-workflow execution inherits the parent run's ambient env.
            Env = parentContext.Env
        };

        await contextStore.SaveContextAsync(childContext);

        return new object();
    }
}
