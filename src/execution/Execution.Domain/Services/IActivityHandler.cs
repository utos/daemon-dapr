using Common.SeedWork;
using Common.Workflow;

namespace Execution.Domain.Services;

public interface IActivityHandler
{
    /// <summary>Which kind of activity this handler runs. The executor dispatches on it.</summary>
    ActivityKind Kind { get; }

    Task<ActivityHandlerResult> ExecuteAsync(ActivityHandlerInput input, CancellationToken ct = default);
}

public record ActivityHandlerInput(
    ExecutionId ExecutionId,
    ExecutionId LogExecutionId,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers);

public record ActivityHandlerResult(
    int StatusCode,
    bool Success,
    object? Output);
