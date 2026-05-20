using Dapr.Workflow;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Execution.Domain.Services;
using Execution.Infrastructure.Extensions;
using Execution.Infrastructure.Templating;
using Utos.Workflow.V1;
using ExecutionContext = Execution.Domain.Services.ExecutionContext;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ResolvePromiseInput(
    Guid ExecutionId,
    string WorkflowKey,
    string ActivityName,
    Dictionary<string, object>? ActivityInput);

public sealed record ResolvePromiseOutput(
    string Mode,
    int RequiredCount,
    List<PromiseBranchResolution> Branches);

public sealed record PromiseBranchResolution(
    string BranchName,
    Guid BranchExecutionId,
    string WorkflowKey,
    string StartActivity,
    Dictionary<string, object>? Input);

public class ResolvePromiseActivity(
    IExecutionContextStore contextStore,
    ITemplateEngine templateEngine,
    ILogger<ResolvePromiseActivity> logger)
    : WorkflowActivity<ResolvePromiseInput, ResolvePromiseOutput>
{
    public override async Task<ResolvePromiseOutput> RunAsync(
        WorkflowActivityContext context, ResolvePromiseInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);
        var workflow = executionContext.Bundle.Workflows[input.WorkflowKey];

        if (!workflow.Spec.Activities.TryGetValue(input.ActivityName, out var activity))
            throw new InvalidOperationException(
                $"Activity '{input.ActivityName}' not found in workflow '{input.WorkflowKey}'");

        var promiseConfig = activity.Promise;
        var activityInput = input.ActivityInput ?? executionContext.Input;
        var templateContext = new TemplateContext().SetInput(activityInput);

        var branches = new List<PromiseBranchResolution>();

        foreach (var branch in promiseConfig.Branches)
        {
            // Evaluate condition if set — skip branch if false
            if (branch.HasCondition && !string.IsNullOrEmpty(branch.Condition))
            {
                var shouldInclude = templateEngine.Evaluate<bool>(branch.Condition, templateContext, logger);
                if (!shouldInclude)
                    continue;
            }

            if (branch.ForEach != null)
            {
                // Dynamic expansion: evaluate collection, iterate with alias
                var collection = templateEngine.Evaluate<IEnumerable<object>>(
                    branch.ForEach.Collection, templateContext, logger);

                var index = 0;
                foreach (var item in collection)
                {
                    var itemContext = new TemplateContext()
                        .SetInput(activityInput)
                        .Set(branch.ForEach.Alias, item);

                    var resolution = ResolveBranch(
                        $"{branch.Name}_{index}",
                        branch.Target,
                        itemContext,
                        activityInput,
                        workflow,
                        executionContext);

                    branches.Add(resolution);
                    index++;
                }
            }
            else
            {
                // Static branch: single resolution
                var resolution = ResolveBranch(
                    branch.Name,
                    branch.Target,
                    templateContext,
                    activityInput,
                    workflow,
                    executionContext);

                branches.Add(resolution);
            }
        }

        // Save child contexts for all branches
        foreach (var branch in branches)
        {
            var childContext = new ExecutionContext
            {
                ExecutionId = branch.BranchExecutionId,
                Bundle = executionContext.Bundle,
                EntryPoint = branch.WorkflowKey,
                Input = branch.Input ?? new Dictionary<string, object>()
            };

            await contextStore.SaveContextAsync(childContext);
        }

        return new ResolvePromiseOutput(promiseConfig.Mode, promiseConfig.RequiredCount, branches);
    }

    private PromiseBranchResolution ResolveBranch(
        string branchName,
        TransitionTarget target,
        TemplateContext templateContext,
        object activityInput,
        Workflow workflow,
        ExecutionContext executionContext)
    {
        // The target.Name references an activity in the current workflow.
        // That activity must be a workflow-type activity.
        if (!workflow.Spec.Activities.TryGetValue(target.Name, out var targetActivity))
            throw new InvalidOperationException(
                $"Branch target activity '{target.Name}' not found in workflow");

        if (targetActivity.ConfigCase != WorkflowActivity.ConfigOneofCase.Workflow)
            throw new InvalidOperationException(
                $"Branch target activity '{target.Name}' must be a workflow-type activity");

        var workflowConfig = targetActivity.Workflow;

        // Render branch input from target.Input if set, otherwise pass through activity input
        Dictionary<string, object>? branchInput;
        if (target.Input != null)
            branchInput = RenderStruct(target.Input, templateContext);
        else
            branchInput = activityInput as Dictionary<string, object>;

        return new PromiseBranchResolution(
            branchName,
            Guid.NewGuid(),
            workflowConfig.Workflow,
            workflowConfig.StartActivity,
            branchInput);
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
