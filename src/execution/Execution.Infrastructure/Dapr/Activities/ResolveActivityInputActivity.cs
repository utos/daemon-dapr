using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Services;
using Common.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ResolveActivityInputInput(
    ExecutionId ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput);

public sealed record ResolveActivityInputOutput(
    ActivityKind Kind,
    // HTTP fields (null when not HTTP)
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers,
    // Workflow call fields (null when not workflow)
    string? SubWorkflowKey,
    string? SubStartActivity,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? WorkflowInput,
    // True when the sub-workflow is fire-and-forget: the executor starts it as an independent
    // execution and does not await it. False for HTTP and awaited sub-workflows.
    bool Detached = false);

/// <summary>
/// Durable boundary around <see cref="WorkflowInterpreter.ResolveInput"/>, flattening its
/// result into the shape the executor passes on to the handler that does the work.
/// </summary>
public class ResolveActivityInputActivity(
    IExecutionContextStore contextStore,
    WorkflowInterpreter interpreter)
    : WorkflowActivity<ResolveActivityInputInput, ResolveActivityInputOutput>
{
    public override async Task<ResolveActivityInputOutput> RunAsync(
        WorkflowActivityContext context, ResolveActivityInputInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);
        var session = WorkflowSession.From(executionContext, input.WorkflowKey);

        var resolved = interpreter.ResolveInput(session, input.ActivityName, input.ActivityInput);

        return resolved switch
        {
            ResolvedActivityInput.Http http => new ResolveActivityInputOutput(
                ActivityKind.Http,
                http.Url, http.Method, http.Body, http.Headers,
                null, null, null),

            ResolvedActivityInput.SubWorkflow sub => new ResolveActivityInputOutput(
                ActivityKind.Workflow,
                null, null, null, null,
                sub.WorkflowKey, sub.StartActivity, sub.Input, sub.Detached),

            _ => throw new NotSupportedException(
                $"Unsupported resolved activity input '{resolved.GetType().Name}'")
        };
    }
}
