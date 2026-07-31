using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Execution.Domain.Services;
using Common.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public class ExecuteActivity(IEnumerable<IActivityHandler> handlers)
    : WorkflowActivity<ExecuteActivityInput, ExecuteActivityOutput>
{
    public override async Task<ExecuteActivityOutput> RunAsync(
        WorkflowActivityContext context, ExecuteActivityInput input)
    {
        var handler = handlers.FirstOrDefault(h => h.Kind == input.Kind)
                      ?? throw new NotSupportedException(
                          $"No handler registered for activity config type '{input.Kind}'");

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
    ActivityKind Kind,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers);

public sealed record ExecuteActivityOutput(
    int StatusCode,
    bool Success,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Output);
