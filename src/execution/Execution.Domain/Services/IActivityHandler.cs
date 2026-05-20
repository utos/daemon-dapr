using Utos.Workflow.V1;

namespace Execution.Domain.Services;

public interface IActivityHandler
{
    WorkflowActivity.ConfigOneofCase ConfigCase { get; }

    Task<ActivityHandlerResult> ExecuteAsync(ActivityHandlerInput input, CancellationToken ct = default);
}

public record ActivityHandlerInput(
    Guid ExecutionId,
    Guid LogExecutionId,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers);

public record ActivityHandlerResult(
    int StatusCode,
    bool Success,
    object? Output);
