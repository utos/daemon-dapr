using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Microsoft.Extensions.Logging;
using Execution.Domain.Constants;
using Execution.Infrastructure.Dapr.Activities;
using Utos.Workflow.V1;

namespace Execution.Infrastructure.Dapr;

/// <param name="ActivityInput">
/// Mapped input for <paramref name="StartActivity"/>. Null on a fresh execution, set when
/// resuming after a continue-as-new.
/// </param>
/// <param name="Visited">
/// Activities already executed on this path. Null marks the first segment of an execution;
/// non-null means this instance resumed via continue-as-new, which is what suppresses the
/// duplicate "Active" lifecycle event. Bounded by the workflow's activity count.
/// </param>
/// <param name="LoopedTransitions">
/// Activities re-executed since a loop was entered, carried across continuations so the
/// ceiling survives them.
/// </param>
/// <param name="StartedAt">
/// When the execution originally began, so the completion duration covers the whole run
/// rather than just the final segment.
/// </param>
public record WorkflowExecutorInput(
    Guid ExecutionId,
    string WorkflowKey,
    string StartActivity,
    Guid? ParentExecutionId = null,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput = null,
    List<string>? Visited = null,
    int LoopedTransitions = 0,
    DateTime? StartedAt = null,
    int MaxLoopedTransitions = 10_000,
    int ContinueAsNewAfterTransitions = 100);

public class WorkflowExecutor : Workflow<WorkflowExecutorInput, object?>
{
    public override async Task<object?> RunAsync(WorkflowContext context, WorkflowExecutorInput input)
    {
        var logger = context.CreateReplaySafeLogger<WorkflowExecutor>();
        var isChild = input.ParentExecutionId != null;
        // A non-null Visited set means this instance resumed via continue-as-new rather than
        // being scheduled fresh, so the run's opening bookkeeping must not repeat.
        var isContinuation = input.Visited != null;
        var startTime = input.StartedAt ?? context.CurrentUtcDateTime;
        // Child workflows log under the parent's execution ID so their entries appear in the parent's WatchExecution stream.
        var logExecutionId = input.ParentExecutionId ?? input.ExecutionId;

        if (!isChild && !isContinuation)
            await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                new WriteLifecycleEventInput(input.ExecutionId, "Active", null));

        if (!isContinuation)
            logger.LogInformation(
                "Workflow execution started from activity '{WorkflowStartActivity}' (execution {WorkflowExecutionId})",
                input.StartActivity, logExecutionId);

        try
        {
            // Load the workflow definition once at start (avoids per-activity DB lookups)
            var workflowInfo = await context.CallActivityAsync<LoadWorkflowOutput>(
                nameof(LoadWorkflowActivity),
                new LoadWorkflowInput(input.ExecutionId, input.WorkflowKey));
            var workflow = workflowInfo.Workflow;

            var currentActivityName = input.StartActivity;
            var currentActivityInput = input.ActivityInput;
            object? workflowOutput = null;

            // Ordinal to match how Spec.Activities (a protobuf MapField) is keyed below.
            var visited = new HashSet<string>(input.Visited ?? [], StringComparer.Ordinal);
            var loopedTransitions = input.LoopedTransitions;
            var transitionsThisSegment = 0;
            var loopDetected = input.Visited != null && input.LoopedTransitions > 0;

            while (currentActivityName != null)
            {
                if (string.Equals(currentActivityName, ReservedKeywords.End, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Workflow reached 'end' transition (execution {WorkflowExecutionId})", logExecutionId);
                    break;
                }

                if (string.Equals(currentActivityName, ReservedKeywords.Error, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Workflow transitioned to 'error'");

                if (!workflow.Spec.Activities.TryGetValue(currentActivityName, out var activity))
                    throw new InvalidOperationException(
                        $"Activity '{currentActivityName}' not found in workflow '{input.WorkflowKey}'");

                // The executor walks a single path — promise branches and sub-workflows are
                // separate child executions — so reaching an activity twice can only mean a
                // back-edge.
                if (!visited.Add(currentActivityName))
                {
                    loopedTransitions++;

                    if (!loopDetected)
                    {
                        loopDetected = true;
                        logger.LogInformation(
                            "Loop detected at activity '{WorkflowActivity}' (execution {WorkflowExecutionId})",
                            currentActivityName, logExecutionId);
                    }

                    if (loopedTransitions >= input.MaxLoopedTransitions)
                        throw new InvalidOperationException(
                            $"Workflow exceeded the maximum of {input.MaxLoopedTransitions} looped activity " +
                            $"executions; the loop re-enters activity '{currentActivityName}'. Either the " +
                            $"loop has no reachable exit, or the limit is too low for this workflow.");
                }

                transitionsThisSegment++;

                switch (activity.ConfigCase)
                {
                    case WorkflowActivity.ConfigOneofCase.Workflow:
                    {
                        logger.LogInformation(
                            "Entering sub-workflow from activity '{WorkflowActivity}' (execution {WorkflowExecutionId})",
                            currentActivityName, logExecutionId);

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
                        // The child inherits the limits but starts with its own empty visited
                        // set and budget — a sub-workflow invoked twice from a parent loop is
                        // two separate executions, not a back-edge.
                        var childOutput = await context.CallChildWorkflowAsync<object?>(
                            nameof(WorkflowExecutor),
                            new WorkflowExecutorInput(childExecutionId, resolved.SubWorkflowKey!,
                                resolved.SubStartActivity!, input.ExecutionId,
                                MaxLoopedTransitions: input.MaxLoopedTransitions,
                                ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions),
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
                        logger.LogInformation(
                            "Activity '{WorkflowActivity}' started (execution {WorkflowExecutionId})",
                            currentActivityName, logExecutionId);

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

                            logger.LogInformation(
                                "Activity '{WorkflowActivity}' completed in {DurationMs}ms (execution {WorkflowExecutionId})",
                                currentActivityName, (long)activityDuration, logExecutionId);

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
                            logger.LogError(
                                "Activity '{WorkflowActivity}' failed: {ErrorMessage} (execution {WorkflowExecutionId})",
                                currentActivityName, activityEx.Message, logExecutionId);
                            throw;
                        }

                        break;
                    }

                    case WorkflowActivity.ConfigOneofCase.Promise:
                    {
                        logger.LogInformation(
                            "Entering promise activity '{WorkflowActivity}' (mode: {PromiseMode}) (execution {WorkflowExecutionId})",
                            currentActivityName, activity.Promise.Mode, logExecutionId);

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
                                    branch.StartActivity, input.ExecutionId,
                                    MaxLoopedTransitions: input.MaxLoopedTransitions,
                                    ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions),
                                new ChildWorkflowTaskOptions(branch.BranchExecutionId.ToString()));
                            branchTasks[branch.BranchName] = task;
                        }

                        // 3. Resolve by mode
                        var promiseOutput = await ResolveByMode(
                            resolved.Mode, resolved.RequiredCount, branchTasks);

                        logger.LogInformation(
                            "Promise activity '{WorkflowActivity}' resolved ({PromiseMode} mode, {BranchCount} branches) (execution {WorkflowExecutionId})",
                            currentActivityName, resolved.Mode, resolved.Branches.Count, logExecutionId);

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

                // Dapr appends to instance history on every activity call, so a loop grows it
                // without bound. Continuing as new resets that history while carrying the loop
                // state forward. Only once looping: continuing as new discards history, which
                // is destructive for a long non-looping run someone may be debugging.
                if (loopDetected
                    && currentActivityName != null
                    && !ReservedKeywords.IsReserved(currentActivityName)
                    && transitionsThisSegment >= input.ContinueAsNewAfterTransitions)
                {
                    logger.LogDebug(
                        "Continuing as new at activity '{WorkflowActivity}' after {TransitionCount} transitions (execution {WorkflowExecutionId})",
                        currentActivityName, transitionsThisSegment, logExecutionId);

                    context.ContinueAsNew(
                        input with
                        {
                            StartActivity = currentActivityName,
                            ActivityInput = currentActivityInput,
                            Visited = [..visited],
                            LoopedTransitions = loopedTransitions,
                            StartedAt = startTime
                        },
                        preserveUnprocessedEvents: true);

                    return null;
                }
            }

            var totalDuration = (context.CurrentUtcDateTime - startTime).TotalMilliseconds;

            logger.LogInformation(
                "Workflow execution completed successfully in {DurationMs}ms (execution {WorkflowExecutionId})",
                (long)totalDuration, logExecutionId);

            if (!isChild)
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, "Completed", null));

            return workflowOutput;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Workflow execution failed: {ErrorMessage} (execution {WorkflowExecutionId})",
                ex.Message, logExecutionId);

            if (!isChild)
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, "Failed", ex.Message));

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
