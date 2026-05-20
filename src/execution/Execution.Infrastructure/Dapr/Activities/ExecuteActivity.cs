using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Execution.Domain.Services;
using Utos.Workflow.V1;

namespace Execution.Infrastructure.Dapr.Activities;

public class ExecuteActivity(IEnumerable<IActivityHandler> handlers)
    : WorkflowActivity<ExecuteActivityInput, ExecuteActivityOutput>
{
    public override async Task<ExecuteActivityOutput> RunAsync(
        WorkflowActivityContext context, ExecuteActivityInput input)
    {
        var handler = handlers.FirstOrDefault(h => h.ConfigCase == input.ConfigCase)
                      ?? throw new NotSupportedException(
                          $"No handler registered for activity config type '{input.ConfigCase}'");

        var result = await handler.ExecuteAsync(
            new ActivityHandlerInput(
                input.ExecutionId,
                input.LogExecutionId,
                input.Url,
                input.HttpMethod,
                input.Body,
                input.Headers));

        return new ExecuteActivityOutput(result.StatusCode, result.Success, result.Output);
    }
}

public sealed record ExecuteActivityInput(
    Guid ExecutionId,
    Guid LogExecutionId,
    WorkflowActivity.ConfigOneofCase ConfigCase,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers);

public sealed record ExecuteActivityOutput(
    int StatusCode,
    bool Success,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Output);
