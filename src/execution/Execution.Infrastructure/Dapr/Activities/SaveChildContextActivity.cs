using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Services;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace Execution.Infrastructure.Dapr.Activities;

/// <param name="Children">
/// Saved in one call rather than one call per child: a promise fanning out over a
/// <c>for_each</c> collection can produce many at once, and every activity call is another
/// entry in the orchestration history.
/// </param>
public sealed record SaveChildContextInput(
    ExecutionId ParentExecutionId,
    List<ChildContextSpec> Children);

public sealed record ChildContextSpec(
    ExecutionId ChildExecutionId,
    string WorkflowKey,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Input);

public class SaveChildContextActivity(IExecutionContextStore contextStore)
    : WorkflowActivity<SaveChildContextInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, SaveChildContextInput input)
    {
        var parentContext = await contextStore.LoadContextAsync(input.ParentExecutionId);

        foreach (var child in input.Children)
        {
            var childContext = new ExecutionContext
            {
                ExecutionId = child.ChildExecutionId,
                // The root, not this parent: a grandchild records the same top-level run, so one
                // query reaches the whole tree when it comes time to reclaim it.
                RootExecutionId = parentContext.RootExecutionId ?? parentContext.ExecutionId,
                Bundle = parentContext.Bundle,
                EntryPoint = child.WorkflowKey,
                Input = child.Input ?? new Dictionary<string, object>(),
                // A child execution inherits the parent run's ambient env. Promise branches
                // are children too — they used to be built without it, which left
                // {{ env.x }} undefined inside a branch.
                Env = parentContext.Env
            };

            await contextStore.SaveContextAsync(childContext);
        }

        return new object();
    }
}
