using Common.SeedWork;
using Dapr.Workflow;
using Microsoft.Extensions.Logging;
using Execution.Domain.Constants;
using Execution.Infrastructure.Dapr.Activities;
using Utos.Workflow.V1;

namespace Execution.Infrastructure.Dapr;

public record WorkflowExecutorInput(
    Guid ExecutionId,
    string WorkflowKey,
    string StartActivity,
    Guid? ParentExecutionId = null);

public class WorkflowExecutor : Workflow<WorkflowExecutorInput, object?>
{
    public override async Task<object?> RunAsync(WorkflowContext context, WorkflowExecutorInput input)
    {
        var logger = context.CreateReplaySafeLogger<WorkflowExecutor>();
        var startTime = context.CurrentUtcDateTime;
        var isChild = input.ParentExecutionId != null;
        var logExecutionId = input.ParentExecutionId ?? input.ExecutionId;

        if (!isChild)
            await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                new WriteLifecycleEventInput(input.ExecutionId, "Active", null));

        await context.CallActivityAsync(nameof(LogActivity), new LogInput(
            logExecutionId, "system", "workflow", WorkflowLogLevel.Info,
            $"Workflow execution started from activity '{input.StartActivity}'"));

        try
        {
            // Load the workflow definition once at start (avoids per-activity DB lookups)
            var workflowInfo = await context.CallActivityAsync<LoadWorkflowOutput>(
                nameof(LoadWorkflowActivity),
                new LoadWorkflowInput(input.ExecutionId, input.WorkflowKey));
            var workflow = workflowInfo.Workflow;

            var currentActivityName = input.StartActivity;
            Dictionary<string, object>? currentActivityInput = null;
            object? workflowOutput = null;

            // TODO: Implement "continue as new" when we detect loops to prevent massive execution logs

            while (currentActivityName != null)
            {
                if (string.Equals(currentActivityName, ReservedKeywords.End, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("[{ExecutionId}] Workflow reached 'end' transition", input.ExecutionId);
                    break;
                }

                if (string.Equals(currentActivityName, ReservedKeywords.Error, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Workflow transitioned to 'error'");

                if (!workflow.Spec.Activities.TryGetValue(currentActivityName, out var activity))
                    throw new InvalidOperationException(
                        $"Activity '{currentActivityName}' not found in workflow '{input.WorkflowKey}'");

                switch (activity.ConfigCase)
                {
                    case WorkflowActivity.ConfigOneofCase.Workflow:
                    {
                        logger.LogInformation(
                            "[{ExecutionId}] Entering sub-workflow from activity '{Activity}'",
                            input.ExecutionId, currentActivityName);

                        // Resolve workflow call input via central template rendering
                        var resolved = await context.CallActivityAsync<ResolveActivityInputOutput>(
                            nameof(ResolveActivityInputActivity),
                            new ResolveActivityInputInput(input.ExecutionId, input.WorkflowKey,
                                currentActivityName, currentActivityInput));

                        var childExecutionId = context.NewGuid();

                        // Save child execution context (copies parent's bundle)
                        await context.CallActivityAsync(nameof(SaveChildContextActivity),
                            new SaveChildContextInput(input.ExecutionId, childExecutionId,
                                resolved.SubWorkflowKey!, resolved.WorkflowInput));

                        // Launch and await child workflow via Dapr's native child workflow support
                        var childOutput = await context.CallChildWorkflowAsync<object?>(
                            nameof(WorkflowExecutor),
                            new WorkflowExecutorInput(childExecutionId, resolved.SubWorkflowKey!,
                                resolved.SubStartActivity!, input.ExecutionId),
                            new ChildWorkflowTaskOptions(childExecutionId.ToString()));

                        // Evaluate parent's transition with child's output
                        var transition = await context.CallActivityAsync<EvaluateTransitionOutput>(
                            nameof(EvaluateTransitionActivity),
                            new EvaluateTransitionInput(input.ExecutionId, input.WorkflowKey,
                                currentActivityName, currentActivityInput, childOutput));

                        if (transition.ReturnValue != null)
                        {
                            workflowOutput = transition.ReturnValue;
                            currentActivityName = null;
                            break;
                        }

                        currentActivityName = transition.NextActivityName;
                        currentActivityInput = transition.MappedInput;
                        break;
                    }

                    case WorkflowActivity.ConfigOneofCase.Http:
                    {
                        await context.CallActivityAsync(nameof(LogActivity), new LogInput(
                            logExecutionId, "system", "activity", WorkflowLogLevel.Info,
                            $"Activity '{currentActivityName}' started"));

                        var activityStartTime = context.CurrentUtcDateTime;

                        // Resolve templates centrally
                        var resolved = await context.CallActivityAsync<ResolveActivityInputOutput>(
                            nameof(ResolveActivityInputActivity),
                            new ResolveActivityInputInput(input.ExecutionId, input.WorkflowKey,
                                currentActivityName, currentActivityInput));

                        try
                        {
                            // TODO: Add retry policy wrapper here before executing activity
                            // TODO: Add on-error handler support to catch and handle specific exceptions
                            var result = await context.CallActivityAsync<ExecuteActivityOutput>(
                                nameof(ExecuteActivity),
                                new ExecuteActivityInput(input.ExecutionId, logExecutionId,
                                    resolved.ConfigCase,
                                    resolved.Url, resolved.HttpMethod, resolved.Body, resolved.Headers));

                            var activityDuration = (context.CurrentUtcDateTime - activityStartTime).TotalMilliseconds;

                            await context.CallActivityAsync(nameof(LogActivity), new LogInput(
                                logExecutionId, "system", "activity", WorkflowLogLevel.Info,
                                $"Activity '{currentActivityName}' completed successfully in {activityDuration:F0}ms"));

                            var transition = await context.CallActivityAsync<EvaluateTransitionOutput>(
                                nameof(EvaluateTransitionActivity),
                                new EvaluateTransitionInput(input.ExecutionId, input.WorkflowKey,
                                    currentActivityName, currentActivityInput, result.Output));

                            if (transition.ReturnValue != null)
                            {
                                workflowOutput = transition.ReturnValue;
                                currentActivityName = null;
                                break;
                            }

                            currentActivityName = transition.NextActivityName;
                            currentActivityInput = transition.MappedInput;
                        }
                        catch (Exception activityEx)
                        {
                            await context.CallActivityAsync(nameof(LogActivity), new LogInput(
                                logExecutionId, "system", "activity", WorkflowLogLevel.Error,
                                $"Activity '{currentActivityName}' failed: {activityEx.Message}"));
                            throw;
                        }

                        break;
                    }

                    case WorkflowActivity.ConfigOneofCase.Promise:
                    {
                        logger.LogInformation(
                            "[{ExecutionId}] Entering promise activity '{Activity}' (mode: {Mode})",
                            input.ExecutionId, currentActivityName, activity.Promise.Mode);

                        // 1. Resolve branches (expand for_each, filter conditions, save child contexts)
                        var resolved = await context.CallActivityAsync<ResolvePromiseOutput>(
                            nameof(ResolvePromiseActivity),
                            new ResolvePromiseInput(input.ExecutionId, input.WorkflowKey,
                                currentActivityName, currentActivityInput));

                        // 2. Fan out — launch all branches as child workflows
                        var branchTasks = new Dictionary<string, Task<object?>>();
                        foreach (var branch in resolved.Branches)
                        {
                            var task = context.CallChildWorkflowAsync<object?>(
                                nameof(WorkflowExecutor),
                                new WorkflowExecutorInput(branch.BranchExecutionId, branch.WorkflowKey,
                                    branch.StartActivity, input.ExecutionId),
                                new ChildWorkflowTaskOptions(branch.BranchExecutionId.ToString()));
                            branchTasks[branch.BranchName] = task;
                        }

                        // 3. Resolve by mode
                        var promiseOutput = await ResolveByMode(
                            resolved.Mode, resolved.RequiredCount, branchTasks);

                        await context.CallActivityAsync(nameof(LogActivity), new LogInput(
                            logExecutionId, "system", "activity", WorkflowLogLevel.Info,
                            $"Promise activity '{currentActivityName}' resolved ({resolved.Mode} mode, {resolved.Branches.Count} branches)"));

                        // 4. Evaluate transition with promise output
                        var transition = await context.CallActivityAsync<EvaluateTransitionOutput>(
                            nameof(EvaluateTransitionActivity),
                            new EvaluateTransitionInput(input.ExecutionId, input.WorkflowKey,
                                currentActivityName, currentActivityInput, promiseOutput));

                        if (transition.ReturnValue != null)
                        {
                            workflowOutput = transition.ReturnValue;
                            currentActivityName = null;
                            break;
                        }

                        currentActivityName = transition.NextActivityName;
                        currentActivityInput = transition.MappedInput;
                        break;
                    }

                    default:
                        throw new NotSupportedException(
                            $"Unsupported activity config type '{activity.ConfigCase.ToString()}' for activity '{currentActivityName}'");
                }

                // Check if we broke out of the switch with a return value
                if (workflowOutput != null)
                    break;
            }

            var totalDuration = (context.CurrentUtcDateTime - startTime).TotalMilliseconds;

            await context.CallActivityAsync(nameof(LogActivity), new LogInput(
                logExecutionId, "system", "workflow", WorkflowLogLevel.Info,
                $"Workflow execution completed successfully in {totalDuration:F0}ms"));

            if (!isChild)
            {
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, "Completed", null));

                await context.CallActivityAsync(nameof(TerminateLogActivity),
                    new TerminateLogInput(input.ExecutionId));
            }

            return workflowOutput;
        }
        catch (Exception ex)
        {
            await context.CallActivityAsync(nameof(LogActivity), new LogInput(
                logExecutionId, "system", "workflow", WorkflowLogLevel.Error,
                $"Workflow execution failed: {ex.Message}"));

            if (!isChild)
            {
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, "Failed", ex.Message));

                await context.CallActivityAsync(nameof(TerminateLogActivity),
                    new TerminateLogInput(input.ExecutionId));
            }

            throw;
        }
    }

    private static async Task<Dictionary<string, object?>> ResolveByMode(
        string mode, int requiredCount,
        Dictionary<string, Task<object?>> branchTasks)
    {
        switch (mode)
        {
            case "all":
            {
                await Task.WhenAll(branchTasks.Values);
                return branchTasks.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
            }

            case "any":
            {
                var remaining = new Dictionary<string, Task<object?>>(branchTasks);
                while (remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining.Values);
                    var winner = remaining.First(kv => kv.Value == completed);
                    remaining.Remove(winner.Key);

                    if (completed.IsCompletedSuccessfully)
                        return new Dictionary<string, object?> { [winner.Key] = completed.Result };
                }

                throw new AggregateException("All branches failed (mode: any)");
            }

            case "race":
            {
                var completed = await Task.WhenAny(branchTasks.Values);
                var winner = branchTasks.First(kv => kv.Value == completed);
                return new Dictionary<string, object?> { [winner.Key] = await completed };
            }

            case "count":
            {
                var remaining = new Dictionary<string, Task<object?>>(branchTasks);
                var results = new Dictionary<string, object?>();

                while (results.Count < requiredCount && remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining.Values);
                    var settled = remaining.First(kv => kv.Value == completed);
                    remaining.Remove(settled.Key);

                    if (completed.IsCompletedSuccessfully)
                        results[settled.Key] = completed.Result;
                    else if (remaining.Count + results.Count < requiredCount)
                        throw new InvalidOperationException(
                            $"Cannot reach required count {requiredCount} (mode: count)");
                }

                if (results.Count < requiredCount)
                    throw new InvalidOperationException(
                        $"Cannot reach required count {requiredCount} (mode: count)");

                return results;
            }

            default:
                throw new NotSupportedException($"Unsupported promise mode: {mode}");
        }
    }
}
