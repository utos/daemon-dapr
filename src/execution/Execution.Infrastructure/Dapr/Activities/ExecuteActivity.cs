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
                input.Headers,
                input.LogSource,
                // The run tree and the workflow that produced it, so a blob this activity stores is
                // attached to the tree and its provenance recorded. The root defaults to this
                // execution, which is what a root execution's own root is.
                input.RootExecutionId ?? input.ExecutionId,
                input.Workflow,
                input.WorkflowDigest,
                input.ActivityName));

        return new ExecuteActivityOutput(
            result.StatusCode, result.Success, result.Output, result.Headers, result.Body,
            result.BodySummary);
    }
}

public sealed record ExecuteActivityInput(
    ExecutionId ExecutionId,
    ExecutionId LogExecutionId,
    ActivityKind Kind,
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers,
    string? LogSource = null,
    ExecutionId? RootExecutionId = null,
    string? Workflow = null,
    string? WorkflowDigest = null,
    string? ActivityName = null);

/// <param name="Headers">
/// Plain string-to-string, so no custom converter is needed — unlike <paramref name="Output"/>,
/// which carries arbitrary workflow values and would come back as JsonElement without one.
/// </param>
/// <param name="Body">
/// The response body as a <c>Blob</c>.
/// <para>
/// <b>An inline blob's bytes are written into orchestration history here</b>, base64, once per
/// transition that carries the response. That is the cost the inline threshold exists to bound, and
/// why it is a few hundred kibibytes rather than the megabytes the response cap allows: above it the
/// value is a handle of a few dozen bytes and the payload sits once in object storage.
/// </para>
/// <para>
/// It used to be held twice — the raw bytes beside an eagerly decoded <c>BodyText</c> — and the
/// second copy is gone with the retired member.
/// </para>
/// </param>
public sealed record ExecuteActivityOutput(
    int StatusCode,
    bool Success,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Output,
    Dictionary<string, string> Headers,
    WorkflowBlob? Body = null,
    string? BodySummary = null);
