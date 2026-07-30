using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Execution.Domain.Services;
using Execution.Domain.Validation;
using Execution.Infrastructure.Extensions;
using Execution.Infrastructure.Templating;
using Utos.Workflow.V1;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ResolveActivityInputInput(
    Guid ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput);

public sealed record ResolveActivityInputOutput(
    WorkflowActivity.ConfigOneofCase ConfigCase,
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

public class ResolveActivityInputActivity(
    IExecutionContextStore contextStore,
    ITemplateEngine templateEngine,
    ILogger<ResolveActivityInputActivity> logger)
    : WorkflowActivity<ResolveActivityInputInput, ResolveActivityInputOutput>
{
    public override async Task<ResolveActivityInputOutput> RunAsync(
        WorkflowActivityContext context, ResolveActivityInputInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);
        var workflow = executionContext.Bundle.Workflows[input.WorkflowKey];

        if (!workflow.Spec.Activities.TryGetValue(input.ActivityName, out var activity))
            throw new InvalidOperationException(
                $"Activity '{input.ActivityName}' not found in workflow '{input.WorkflowKey}'");

        var activityInput = input.ActivityInput ?? executionContext.Input;
        var templateContext = new TemplateContext()
            .SetInput(activityInput)
            .SetEnv(executionContext.Env);

        return activity.ConfigCase switch
        {
            WorkflowActivity.ConfigOneofCase.Http =>
                ResolveHttpActivity(activity.Http, templateContext, input.ActivityName),

            WorkflowActivity.ConfigOneofCase.Workflow =>
                ResolveWorkflowActivity(activity.Workflow, templateContext, activityInput, input.ActivityInput),

            _ => throw new NotSupportedException(
                $"Unsupported activity config type '{activity.ConfigCase}' for activity '{input.ActivityName}'")
        };
    }

    private ResolveActivityInputOutput ResolveHttpActivity(
        HttpActivityConfig httpConfig,
        TemplateContext templateContext,
        string activityName)
    {
        var url = templateEngine.RenderToString(httpConfig.Url, templateContext, logger);

        // A templated URL skips the load-time shape check, since its scheme or host may
        // come from {{ env.x }}. Re-apply the same rules now that it is rendered, so a bad
        // value fails with the same diagnostic instead of a raw HTTP client error.
        if (!HttpUrlRules.IsValidRendered(url))
            throw new InvalidOperationException(
                $"Activity '{activityName}' resolved to an unusable URL '{url}': " +
                $"{HttpUrlRules.GetError(url)}");

        var headers = new Dictionary<string, string>();
        if (httpConfig.Headers.Count > 0)
            foreach (var (key, valueTemplate) in httpConfig.Headers)
                headers[key] = templateEngine.RenderToString(valueTemplate, templateContext, logger);

        string? body = null;
        if (httpConfig.Method is "POST" or "PUT" && httpConfig.HasBody)
            body = templateEngine.RenderToString(httpConfig.Body, templateContext, logger);

        return new ResolveActivityInputOutput(
            WorkflowActivity.ConfigOneofCase.Http,
            url,
            httpConfig.Method,
            body,
            headers,
            null,
            null,
            null);
    }

    private ResolveActivityInputOutput ResolveWorkflowActivity(
        WorkflowActivityConfig config,
        TemplateContext templateContext,
        object activityInput,
        Dictionary<string, object>? rawActivityInput)
    {
        Dictionary<string, object>? subInput;

        if (config.Input != null)
            subInput = RenderStruct(config.Input, templateContext);
        else
            subInput = rawActivityInput;

        return new ResolveActivityInputOutput(
            WorkflowActivity.ConfigOneofCase.Workflow,
            null,
            null,
            null,
            null,
            config.Workflow,
            config.StartActivity,
            subInput,
            config.Detached);
    }

    private Dictionary<string, object> RenderStruct(Struct protoStruct, TemplateContext templateContext)
    {
        var result = new Dictionary<string, object>();

        foreach (var (key, value) in protoStruct.Fields)
        {
            var rendered = RenderValue(value, templateContext);
            if (rendered != null)
                result[key] = rendered;
        }

        return result;
    }

    private object? RenderValue(Value value, TemplateContext templateContext)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => templateEngine.RenderTyped(value.StringValue, templateContext, logger),
            Value.KindOneofCase.StructValue => RenderStruct(value.StructValue, templateContext),
            Value.KindOneofCase.ListValue => value.ListValue.Values
                .Select(v => RenderValue(v, templateContext))
                .ToList(),
            _ => value.ToObject()
        };
    }
}
