using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
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

        return new ExecuteActivityOutput(
            result.StatusCode, result.Success, result.Output, result.Headers, result.BodyText);
    }
}

public sealed record ExecuteActivityInput(
    ExecutionId ExecutionId,
    ExecutionId LogExecutionId,
    ActivityKind Kind,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers);

/// <param name="Headers">
/// Plain string-to-string, so no custom converter is needed — unlike <paramref name="Output"/>,
/// which carries arbitrary workflow values and would come back as JsonElement without one.
/// </param>
public sealed record ExecuteActivityOutput(
    int StatusCode,
    bool Success,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Output,
    Dictionary<string, string> Headers,
    string? BodyText);
