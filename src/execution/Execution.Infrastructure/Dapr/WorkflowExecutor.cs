using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Aggregates;
using Execution.Domain.Interpreter;
using Execution.Infrastructure.Dapr.Activities;
using Microsoft.Extensions.Logging;
using Common.Workflow;

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
/// <param name="LoopDetected">
/// A back-edge has been taken, carried across continuations because it is what makes a segment
/// eligible to continue as new at all.
/// </param>
/// <param name="StartedAt">
/// When the execution originally began, so the completion duration covers the whole run
/// rather than just the final segment.
/// </param>
public record WorkflowExecutorInput(
    ExecutionId ExecutionId,
    string WorkflowKey,
    string StartActivity,
    ExecutionId? ParentExecutionId = null,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput = null,
    List<string>? Visited = null,
    bool LoopDetected = false,
    DateTime? StartedAt = null,
    int ContinueAsNewAfterTransitions = 100);

/// <summary>
/// The durable driver for a workflow execution. It owns nothing about what a workflow
/// *means* — <see cref="WorkflowNavigator"/> decides where the execution goes and
/// <see cref="PromiseResolver"/> decides when a fan-out is satisfied. What is left here is
/// the part that genuinely needs a durable orchestration runtime: recording each side effect
/// so it is performed once, waiting without holding a thread, and surviving a restart.
/// </summary>
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
                new WriteLifecycleEventInput(input.ExecutionId, ExecutionLifecycleTransition.Active, null));

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

            var graph = new WorkflowGraph(workflowInfo.Workflow, input.WorkflowKey);
            var limits = new ExecutionLimits(input.ContinueAsNewAfterTransitions);
            var cursor = ExecutionCursor.Start(input.Visited, input.LoopDetected);

            var currentActivityName = input.StartActivity;
            var currentActivityInput = input.ActivityInput;
            object? workflowOutput = null;

            while (currentActivityName != null)
            {
                var step = WorkflowNavigator.Next(graph, currentActivityName, cursor, limits);

                if (step is WorkflowStep.Complete)
                {
                    logger.LogInformation("Workflow reached 'end' transition (execution {WorkflowExecutionId})", logExecutionId);
                    break;
                }

                if (step is WorkflowStep.Fail failure)
                    throw new InvalidOperationException(failure.Reason);

                if (step is WorkflowStep.ContinueAsNew continuation)
                {
                    // Dapr appends to instance history on every activity call, so a loop grows
                    // it without bound. Continuing as new resets that history while carrying
                    // the loop state forward.
                    logger.LogDebug(
                        "Continuing as new at activity '{WorkflowActivity}' after {TransitionCount} transitions (execution {WorkflowExecutionId})",
                        currentActivityName, continuation.Cursor.TransitionsThisSegment, logExecutionId);

                    context.ContinueAsNew(
                        input with
                        {
                            StartActivity = currentActivityName,
                            ActivityInput = currentActivityInput,
                            Visited = [..continuation.Cursor.Visited],
                            LoopDetected = continuation.Cursor.LoopDetected,
                            StartedAt = startTime
                        },
                        preserveUnprocessedEvents: true);

                    return null;
                }

                var run = (WorkflowStep.Run)step;
                cursor = run.Cursor;

                if (run.LoopJustDetected)
                    logger.LogInformation(
                        "Loop detected at activity '{WorkflowActivity}' (execution {WorkflowExecutionId})",
                        run.ActivityName, logExecutionId);

                var activityOutput = await PerformAsync(
                    context, logger, input, run, currentActivityInput, logExecutionId);

                var transition = await context.CallActivityAsync<EvaluateTransitionOutput>(
                    nameof(EvaluateTransitionActivity),
                    new EvaluateTransitionInput(input.ExecutionId, input.WorkflowKey,
                        run.ActivityName, currentActivityInput, activityOutput));

                // An emit is emit-and-continue: the value becomes durable before the run moves
                // on, so a consumer reading the stream in order can never see the effect of a
                // value it has not been given yet.
                if (transition.EmittedValue != null)
                    await context.CallActivityAsync(nameof(AppendOutputEntryActivity),
                        new AppendOutputEntryInput(input.ExecutionId, OutputEntryKind.Value,
                            transition.EmittedValue));

                if (transition.ReturnValue != null)
                {
                    workflowOutput = transition.ReturnValue;
                    break;
                }

                currentActivityName = transition.NextActivityName;
                currentActivityInput = transition.MappedInput;
            }

            var totalDuration = (context.CurrentUtcDateTime - startTime).TotalMilliseconds;

            logger.LogInformation(
                "Workflow execution completed successfully in {DurationMs}ms (execution {WorkflowExecutionId})",
                (long)totalDuration, logExecutionId);

            // Every execution terminates its own stream, children included: a stream is
            // per-execution, and the caller consuming a child's is what branch B is built on.
            // Written before the lifecycle event, not after — a client that waits for the run to
            // finish is waiting on that event, and must find the stream already closed when it
            // reads it.
            await context.CallActivityAsync(nameof(AppendOutputEntryActivity),
                new AppendOutputEntryInput(input.ExecutionId, OutputEntryKind.Result, workflowOutput));

            if (!isChild)
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, ExecutionLifecycleTransition.Completed, null));

            return workflowOutput;
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Workflow execution failed: {ErrorMessage} (execution {WorkflowExecutionId})",
                ex.Message, logExecutionId);

            await context.CallActivityAsync(nameof(AppendOutputEntryActivity),
                new AppendOutputEntryInput(input.ExecutionId, OutputEntryKind.Error,
                    ErrorMessage: ex.Message));

            if (!isChild)
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, ExecutionLifecycleTransition.Failed, ex.Message));

            throw;
        }
    }

    /// <summary>
    /// Performs the activity's work and returns its output, which the transition is then
    /// evaluated against. Each arm is a different durable primitive; the decisions around
    /// them have already been made.
    /// </summary>
    private async Task<object?> PerformAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        WorkflowStep.Run run,
        Dictionary<string, object>? activityInput,
        ExecutionId logExecutionId)
    {
        switch (run.Activity.Config)
        {
            case SubWorkflowActivityConfig:
                return await CallSubWorkflowAsync(context, logger, input, run, activityInput, logExecutionId);

            case HttpActivityConfig:
                return await CallHttpAsync(context, logger, input, run, activityInput, logExecutionId);

            case PromiseActivityConfig:
                return await FanOutAsync(context, logger, input, run, activityInput, logExecutionId);

            case TimerActivityConfig timer:
            {
                // Non-null on anything that passed load-time validation.
                var delay = timer.Duration
                            ?? throw new InvalidOperationException(
                                $"Timer activity '{run.ActivityName}' has no duration.");

                logger.LogInformation(
                    "Timer activity '{WorkflowActivity}' waiting {DelayMs}ms (execution {WorkflowExecutionId})",
                    run.ActivityName, (long)delay.TotalMilliseconds, logExecutionId);

                // Created on the workflow context, not inside an activity: that is what
                // makes the delay durable, surviving a daemon restart and replaying
                // without re-waiting. CurrentUtcDateTime is the replay-safe clock, so
                // the deadline is deterministic.
                await context.CreateTimer(
                    DaprTimerDeadline.WholeSecond(context.CurrentUtcDateTime, delay),
                    CancellationToken.None);

                // A timer performs no work, so it has no output of its own — the interpreter
                // passes its input through instead.
                return null;
            }

            default:
                throw new NotSupportedException(
                    $"Unsupported activity config type '{run.Activity.Kind}' for activity '{run.ActivityName}'");
        }
    }

    private static async Task<object?> CallHttpAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        WorkflowStep.Run run,
        Dictionary<string, object>? activityInput,
        ExecutionId logExecutionId)
    {
        logger.LogInformation(
            "Activity '{WorkflowActivity}' started (execution {WorkflowExecutionId})",
            run.ActivityName, logExecutionId);

        var activityStartTime = context.CurrentUtcDateTime;

        // Resolve templates centrally
        var resolved = await context.CallActivityAsync<ResolveActivityInputOutput>(
            nameof(ResolveActivityInputActivity),
            new ResolveActivityInputInput(input.ExecutionId, input.WorkflowKey,
                run.ActivityName, activityInput));

        try
        {
            // TODO: Add retry policy wrapper here before executing activity
            // TODO: Add on-error handler support to catch and handle specific exceptions
            var result = await context.CallActivityAsync<ExecuteActivityOutput>(
                nameof(ExecuteActivity),
                new ExecuteActivityInput(input.ExecutionId, logExecutionId,
                    resolved.Kind,
                    resolved.Url, resolved.HttpMethod, resolved.Body, resolved.Headers));

            var activityDuration = (context.CurrentUtcDateTime - activityStartTime).TotalMilliseconds;

            logger.LogInformation(
                "Activity '{WorkflowActivity}' completed in {DurationMs}ms (execution {WorkflowExecutionId})",
                run.ActivityName, (long)activityDuration, logExecutionId);

            return result.Output;
        }
        catch (Exception activityEx)
        {
            logger.LogError(
                "Activity '{WorkflowActivity}' failed: {ErrorMessage} (execution {WorkflowExecutionId})",
                run.ActivityName, activityEx.Message, logExecutionId);
            throw;
        }
    }

    private static async Task<object?> CallSubWorkflowAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        WorkflowStep.Run run,
        Dictionary<string, object>? activityInput,
        ExecutionId logExecutionId)
    {
        logger.LogInformation(
            "Entering sub-workflow from activity '{WorkflowActivity}' (execution {WorkflowExecutionId})",
            run.ActivityName, logExecutionId);

        // Resolve workflow call input via central template rendering
        var resolved = await context.CallActivityAsync<ResolveActivityInputOutput>(
            nameof(ResolveActivityInputActivity),
            new ResolveActivityInputInput(input.ExecutionId, input.WorkflowKey,
                run.ActivityName, activityInput));

        // NewGuid is the replay-safe source, so the same child id comes back on replay.
        var childExecutionId = new ExecutionId(context.NewGuid());

        if (resolved.Detached)
        {
            // Fire-and-forget: start the sub-workflow as its own independent, top-level
            // execution and do NOT await it. Only a failure to *start* the child (the
            // activity throwing) surfaces here; the child's own runtime failures never do —
            // it runs on its own. A start failure fails this parent execution, same as any
            // other activity failure (on_failure routing is not yet implemented).
            await context.CallActivityAsync(nameof(ScheduleDetachedExecutionActivity),
                new ScheduleDetachedExecutionInput(input.ExecutionId, childExecutionId,
                    resolved.SubWorkflowKey!, resolved.SubStartActivity!,
                    resolved.WorkflowInput));

            // The activity output is a handle to the started run, not its result.
            return new Dictionary<string, object>
            {
                ["execution_id"] = childExecutionId.ToString()
            };
        }

        // Save child execution context (copies parent's bundle)
        await context.CallActivityAsync(nameof(SaveChildContextActivity),
            new SaveChildContextInput(input.ExecutionId,
                [new ChildContextSpec(childExecutionId, resolved.SubWorkflowKey!, resolved.WorkflowInput)]));

        // Launch and await child workflow via Dapr's native child workflow support.
        // The child inherits the limits but starts with its own empty visited set — a
        // sub-workflow invoked twice from a parent loop is two separate executions, not a
        // back-edge.
        return await context.CallChildWorkflowAsync<object?>(
            nameof(WorkflowExecutor),
            new WorkflowExecutorInput(childExecutionId, resolved.SubWorkflowKey!,
                resolved.SubStartActivity!, input.ExecutionId,
                ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions),
            new ChildWorkflowTaskOptions(childExecutionId.ToString()));
    }

    private static async Task<object?> FanOutAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        WorkflowStep.Run run,
        Dictionary<string, object>? activityInput,
        ExecutionId logExecutionId)
    {
        logger.LogInformation(
            "Entering promise activity '{WorkflowActivity}' (mode: {PromiseMode}) (execution {WorkflowExecutionId})",
            run.ActivityName, ((PromiseActivityConfig)run.Activity.Config).Mode, logExecutionId);

        // 1. Expand branches (apply conditions, expand for_each, render inputs)
        var resolved = await context.CallActivityAsync<ResolvePromiseOutput>(
            nameof(ResolvePromiseActivity),
            new ResolvePromiseInput(input.ExecutionId, input.WorkflowKey,
                run.ActivityName, activityInput));

        // 2. Assign each branch an execution id. Minted here rather than during expansion
        //    because NewGuid is the replay-safe source — the same ids come back on replay.
        var branches = resolved.Branches
            .Select(branch => (Branch: branch, ExecutionId: new ExecutionId(context.NewGuid())))
            .ToList();

        // 3. Persist every branch context in one call, so each branch inherits the parent's
        //    bundle and ambient env before it starts.
        if (branches.Count > 0)
            await context.CallActivityAsync(nameof(SaveChildContextActivity),
                new SaveChildContextInput(input.ExecutionId,
                    branches
                        .Select(b => new ChildContextSpec(b.ExecutionId, b.Branch.WorkflowKey, b.Branch.Input))
                        .ToList()));

        // 4. Fan out — launch all branches as child workflows
        var branchTasks = new Dictionary<string, Task<object?>>(StringComparer.Ordinal);
        foreach (var (branch, branchExecutionId) in branches)
            branchTasks[branch.BranchName] = context.CallChildWorkflowAsync<object?>(
                nameof(WorkflowExecutor),
                new WorkflowExecutorInput(branchExecutionId, branch.WorkflowKey,
                    branch.StartActivity, input.ExecutionId,
                    ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions),
                new ChildWorkflowTaskOptions(branchExecutionId.ToString()));

        // 5. Settle by mode
        var promiseOutput = await SettleAsync(resolved.Mode, resolved.RequiredCount, branchTasks);

        logger.LogInformation(
            "Promise activity '{WorkflowActivity}' resolved ({PromiseMode} mode, {BranchCount} branches) (execution {WorkflowExecutionId})",
            run.ActivityName, ((PromiseActivityConfig)run.Activity.Config).Mode, resolved.Branches.Count, logExecutionId);

        return promiseOutput;
    }

    /// <summary>
    /// Awaits branches one settlement at a time, letting <see cref="PromiseResolver"/> say
    /// when the mode is satisfied. The awaiting is this layer's; which branches decide the
    /// outcome is not.
    /// </summary>
    private static async Task<Dictionary<string, object?>> SettleAsync(
        PromiseMode mode,
        int requiredCount,
        Dictionary<string, Task<object?>> branchTasks)
    {
        var resolver = new PromiseResolver(mode, requiredCount, branchTasks.Count);
        var progress = resolver.Start();

        var remaining = new Dictionary<string, Task<object?>>(branchTasks, StringComparer.Ordinal);

        while (progress is PromiseProgress.Pending && remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining.Values);
            var settled = remaining.First(kv => kv.Value == completed);
            remaining.Remove(settled.Key);

            var outcome = completed.IsCompletedSuccessfully
                ? new BranchOutcome(true, completed.Result)
                : new BranchOutcome(false, null);

            progress = resolver.Observe(settled.Key, outcome);
        }

        switch (progress)
        {
            case PromiseProgress.Resolved resolved:
                return resolved.Results;

            case PromiseProgress.BranchFailed branchFailed:
                // The branch's own error is the useful one, so re-await it to rethrow rather
                // than reporting something invented here.
                await branchTasks[branchFailed.BranchName];
                throw new InvalidOperationException(
                    $"Promise branch '{branchFailed.BranchName}' failed");

            case PromiseProgress.Failed failed:
                throw new InvalidOperationException(failed.Reason);

            default:
                throw new InvalidOperationException(
                    $"Promise did not settle (mode: {mode}, {branchTasks.Count} branches)");
        }
    }
}
