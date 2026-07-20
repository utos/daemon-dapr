using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Execution.Domain.Constants;
using Execution.Domain.Services;
using Execution.Infrastructure.Extensions;
using Execution.Infrastructure.Templating;

namespace Execution.Infrastructure.Dapr.Activities;

public class EvaluateTransitionActivity(
    IExecutionContextStore contextStore,
    ITemplateEngine templateEngine,
    ILogger<EvaluateTransitionActivity> logger)
    : WorkflowActivity<EvaluateTransitionInput, EvaluateTransitionOutput>
{
    public override async Task<EvaluateTransitionOutput> RunAsync(
        WorkflowActivityContext context, EvaluateTransitionInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);

        // Resolve activity definition
        var workflow = executionContext.Bundle.Workflows[input.WorkflowKey];
        if (!workflow.Spec.Activities.TryGetValue(input.ActivityName, out var activity))
            throw new InvalidOperationException(
                $"Activity '{input.ActivityName}' not found in workflow '{input.WorkflowKey}'");

        // Resolve activity input (what this activity received)
        var activityInput = input.ActivityInput ?? executionContext.Input;

        // Activity output passed in directly (no DB lookup)
        var activityOutput = input.ActivityOutput;

        // If no transitions, workflow ends
        if (activity.OnSuccess == null || activity.OnSuccess.Count == 0)
            return new EvaluateTransitionOutput(ReservedKeywords.End, null, null);

        // Build template context with both input and output
        var templateContext = new TemplateContext()
            .SetInput(activityInput)
            .SetOutput(activityOutput)
            .SetEnv(executionContext.Env);

        // Evaluate transitions in order, first matching condition wins
        foreach (var rule in activity.OnSuccess)
        {
            // Check condition first — skip this rule if condition doesn't match
            if (rule.HasCondition && !string.IsNullOrEmpty(rule.Condition))
            {
                var shouldTransition = templateEngine.Evaluate<bool>(rule.Condition, templateContext, logger);
                if (!shouldTransition)
                    continue;
            }

            // Condition matched (or no condition) — execute the action
            return rule.ActionCase switch
            {
                Utos.Workflow.V1.TransitionRule.ActionOneofCase.Transition =>
                    HandleTransition(rule.Transition, templateContext, activityOutput),

                Utos.Workflow.V1.TransitionRule.ActionOneofCase.Result =>
                    HandleResult(rule.Result, templateContext),

                _ => throw new InvalidOperationException(
                    "Transition rule has no action set (neither transition nor result)")
            };
        }

        // No transitions matched - workflow ends
        return new EvaluateTransitionOutput(ReservedKeywords.End, null, null);
    }

    private EvaluateTransitionOutput HandleTransition(
        Utos.Workflow.V1.TransitionTarget target,
        TemplateContext templateContext,
        object? activityOutput)
    {
        var nextActivityName = target.Name;
        var mappedInput = ResolveTransitionInput(target, templateContext, activityOutput);

        return new EvaluateTransitionOutput(nextActivityName, mappedInput, null);
    }

    private EvaluateTransitionOutput HandleResult(
        Struct resultStruct,
        TemplateContext templateContext)
    {
        var rendered = RenderStruct(resultStruct, templateContext);
        return new EvaluateTransitionOutput(ReservedKeywords.End, null, rendered);
    }

    /// <summary>
    /// Resolves the mapped input for the target activity.
    /// If the transition target has an explicit input transform, renders it.
    /// Otherwise, passes the raw activity output through as-is.
    /// </summary>
    private Dictionary<string, object>? ResolveTransitionInput(
        Utos.Workflow.V1.TransitionTarget target,
        TemplateContext templateContext,
        object? activityOutput)
    {
        if (target.Input != null)
            return RenderStruct(target.Input, templateContext);

        // Pass-through: raw output becomes the next activity's input
        return activityOutput as Dictionary<string, object>;
    }

    /// <summary>
    /// Recursively renders template expressions within a protobuf Struct,
    /// producing a Dictionary with resolved values.
    /// </summary>
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
            Value.KindOneofCase.StringValue => RenderStringValue(value.StringValue, templateContext),
            Value.KindOneofCase.StructValue => RenderStruct(value.StructValue, templateContext),
            Value.KindOneofCase.ListValue => value.ListValue.Values
                .Select(v => RenderValue(v, templateContext))
                .ToList(),
            _ => value.ToObject()
        };
    }

    private object RenderStringValue(string str, TemplateContext templateContext)
    {
        if (!templateEngine.HasTemplate(str))
            return str;

        return templateEngine.RenderToString(str, templateContext, logger);
    }
}

public sealed record EvaluateTransitionInput(
    Guid ExecutionId,
    string WorkflowKey,
    string ActivityName,
    Dictionary<string, object>? ActivityInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ActivityOutput);

public sealed record EvaluateTransitionOutput(
    string? NextActivityName,
    Dictionary<string, object>? MappedInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ReturnValue);
