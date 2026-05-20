using System.Text.Json.Serialization;
using Dapr.Workflow;
using Execution.Domain.Serialization;
using Execution.Domain.Services;
using Utos.Workflow.V1;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record LoadWorkflowInput(Guid ExecutionId, string WorkflowKey);

public sealed record LoadWorkflowOutput(
    [property: JsonConverter(typeof(ProtobufJsonConverter<Workflow>))]
    Workflow Workflow);

public class LoadWorkflowActivity(IExecutionContextStore contextStore)
    : WorkflowActivity<LoadWorkflowInput, LoadWorkflowOutput>
{
    public override async Task<LoadWorkflowOutput> RunAsync(
        WorkflowActivityContext context, LoadWorkflowInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);

        if (!executionContext.Bundle.Workflows.TryGetValue(input.WorkflowKey, out var workflow))
            throw new InvalidOperationException(
                $"Workflow '{input.WorkflowKey}' not found in bundle for execution {input.ExecutionId}");

        return new LoadWorkflowOutput(workflow);
    }
}
