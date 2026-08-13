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
/// <param name="Subscriptions">
/// Live output subscriptions — the call activity that opened each, and the child producing for
/// it. Re-entering that activity resumes the subscription rather than starting a second child.
/// </param>
/// <param name="OutputEventName">
/// Set on a child whose caller consumes what it emits, and is both the fact and the address: the
/// channel to hand each value to <see cref="ParentExecutionId"/> on. Null means no privileged
/// consumer, so an emit completes as soon as the entry is durable rather than waiting for an
/// acknowledgement that would never come. Decided once, at start, from the caller's
/// <c>on_emitted</c> — never by asking at emit time who is listening, which could not replay.
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
    int ContinueAsNewAfterTransitions = 100,
    Dictionary<string, ExecutionId>? Subscriptions = null,
    string? OutputEventName = null)
{
    /// <summary>
    /// The envelope for the next segment of this execution — everything that must survive a
    /// <c>ContinueAsNew</c>.
    /// <para>
    /// This record *is* the continuation envelope, and a <c>with</c> expression carries anything
    /// it does not name straight through. That makes the dangerous case a quiet one: state the
    /// segment <em>changed</em> and did not write back here does not reset to a default, it
    /// rewinds to whatever it held when the segment began — every
    /// <see cref="ContinueAsNewAfterTransitions"/> transitions, invisibly. A subscription cursor
    /// that rewinds re-delivers values a caller already handled.
    /// </para>
    /// <para>
    /// So every piece of per-segment state is a required parameter here rather than being read
    /// off <c>this</c>. Adding another one breaks the call site until it is threaded through,
    /// which is the only mechanism that reliably catches the omission — a test cannot see into a
    /// <c>with</c> expression, and the runtime failure is silent.
    /// </para>
    /// </summary>
    public WorkflowExecutorInput ContinueFrom(
        string activityName,
        Dictionary<string, object>? activityInput,
        ExecutionCursor cursor,
        DateTime startedAt,
        Dictionary<string, ExecutionId> subscriptions) =>
        this with
        {
            StartActivity = activityName,
            ActivityInput = activityInput,
            Visited = [..cursor.Visited],
            LoopDetected = cursor.LoopDetected,
            StartedAt = startedAt,
            Subscriptions = subscriptions
        };
}

/// <summary>
/// What an activity produced, and whether that was the end of it. An emission is a value a
/// sub-workflow handed over mid-run, which routes through <c>on_emitted</c>; everything else is a
/// finished activity, routing through <c>on_success</c>. The two are indistinguishable by value
/// alone, which is why they are not carried as one.
/// </summary>
public readonly record struct ActivityOutcome(object? Output, bool IsEmission)
{
    public static ActivityOutcome Completed(object? output) => new(output, false);

    public static ActivityOutcome Emitted(object? value) => new(value, true);
}

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

        // Per-segment state, resumed from the envelope. Mutated as the segment runs and written
        // back by ContinueFrom — see the remarks there for why it is threaded rather than read
        // off `input` at the continuation point. Declared outside the try because a failure
        // abandons subscriptions just as a clean finish does, and the catch needs to see them.
        var subscriptions = new Dictionary<string, ExecutionId>(
            input.Subscriptions ?? [], StringComparer.Ordinal);

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
                        input.ContinueFrom(
                            currentActivityName, currentActivityInput, continuation.Cursor,
                            startTime, subscriptions),
                        preserveUnprocessedEvents: true);

                    return null;
                }

                var run = (WorkflowStep.Run)step;
                cursor = run.Cursor;

                if (run.LoopJustDetected)
                    logger.LogInformation(
                        "Loop detected at activity '{WorkflowActivity}' (execution {WorkflowExecutionId})",
                        run.ActivityName, logExecutionId);

                var outcome = await PerformAsync(
                    context, logger, input, run, currentActivityInput, logExecutionId, subscriptions);

                // An emission routes through on_emitted rather than on_success: the child has
                // produced a value, not finished. Which rule list applies is the interpreter's
                // call; that it was an emission is only knowable here.
                var transition = await context.CallActivityAsync<EvaluateTransitionOutput>(
                    nameof(EvaluateTransitionActivity),
                    new EvaluateTransitionInput(input.ExecutionId, input.WorkflowKey,
                        run.ActivityName, currentActivityInput, outcome.Output, outcome.IsEmission));

                // An emit is emit-and-continue: the value becomes durable before the run moves
                // on, so a consumer reading the stream in order can never see the effect of a
                // value it has not been given yet.
                if (transition.EmittedValue != null)
                {
                    await context.CallActivityAsync(nameof(AppendOutputEntryActivity),
                        new AppendOutputEntryInput(input.ExecutionId, OutputEntryKind.Value,
                            transition.EmittedValue));

                    // Gating, depth one. Hand the value to the privileged consumer and do not
                    // continue until it says so, which bounds the unconsumed buffer to a single
                    // entry however much faster this run is than its caller.
                    //
                    // Only when there *is* such a consumer. A caller that declared no on_emitted
                    // wants the terminal value alone, and waiting for an acknowledgement it will
                    // never send would not fail — it would hang, taking the caller with it.
                    if (input.OutputEventName != null)
                    {
                        await context.CallActivityAsync(nameof(RaiseChildOutputActivity),
                            new RaiseChildOutputInput(
                                input.ParentExecutionId!.Value, input.OutputEventName,
                                new ChildOutputMessage(ChildOutputKind.Value, transition.EmittedValue)));

                        await context.WaitForExternalEventAsync<bool>(OutputEvents.Consumed);
                    }
                }

                if (transition.ReturnValue != null)
                {
                    workflowOutput = transition.ReturnValue;
                    break;
                }

                currentActivityName = transition.NextActivityName;
                currentActivityInput = transition.MappedInput;
            }

            // Anything still subscribed is abandoned: this run left its consuming loop and is not
            // coming back. Before the terminal entry, so a producer is stopped rather than left
            // parked while its caller is recorded as finished.
            await AbandonSubscriptionsAsync(context, logger, subscriptions, logExecutionId);

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

            // The terminal message ends the subscription. A consumer waits on this channel and
            // nothing else, so a child that finished without saying so would leave its caller
            // waiting on a value that is never coming.
            if (input.OutputEventName != null)
                await context.CallActivityAsync(nameof(RaiseChildOutputActivity),
                    new RaiseChildOutputInput(
                        input.ParentExecutionId!.Value, input.OutputEventName,
                        new ChildOutputMessage(ChildOutputKind.Result, workflowOutput)));

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

            // A failure is a terminal message too, and it goes out *first* — ahead of the
            // abandonment sweep and ahead of recording the entry.
            //
            // This is the one ordering in the class that is inverted deliberately. Everywhere
            // else the durable record is written before anyone is told, so a client that hears a
            // run finished can rely on finding it recorded. Here there is a consumer blocked on
            // this exact message, and every step taken before sending it is a step that can throw
            // and strand it forever — appending to an event store that is already unwell is a
            // realistic way to fail at precisely this moment. The entry below is a record nobody
            // is waiting on; the message is one somebody is.
            //
            // Safe against a duplicate terminal message because the success path sends its result
            // only after its append succeeded: exactly one of the two runs to completion.
            if (input.OutputEventName != null)
                await context.CallActivityAsync(nameof(RaiseChildOutputActivity),
                    new RaiseChildOutputInput(
                        input.ParentExecutionId!.Value, input.OutputEventName,
                        new ChildOutputMessage(ChildOutputKind.Error, null, ex.Message)));

            // A failure abandons a subscription just as surely as reaching the end does — more
            // so, since the failure may be the very thing that stopped the run returning to its
            // call activity.
            await AbandonSubscriptionsAsync(context, logger, subscriptions, logExecutionId);

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
    private async Task<ActivityOutcome> PerformAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        WorkflowStep.Run run,
        Dictionary<string, object>? activityInput,
        ExecutionId logExecutionId,
        Dictionary<string, ExecutionId> subscriptions)
    {
        switch (run.Activity.Config)
        {
            case SubWorkflowActivityConfig:
                return await CallSubWorkflowAsync(
                    context, logger, input, run, activityInput, logExecutionId, subscriptions);

            case HttpActivityConfig:
                return ActivityOutcome.Completed(
                    await CallHttpAsync(context, logger, input, run, activityInput, logExecutionId));

            case PromiseActivityConfig:
                return ActivityOutcome.Completed(
                    await FanOutAsync(context, logger, input, run, activityInput, logExecutionId));

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
                return ActivityOutcome.Completed(null);
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

    private static async Task<ActivityOutcome> CallSubWorkflowAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        WorkflowStep.Run run,
        Dictionary<string, object>? activityInput,
        ExecutionId logExecutionId,
        Dictionary<string, ExecutionId> subscriptions)
    {
        // Re-entry into a live subscription. The child is already running and already knows where
        // to send what it produces, so there is nothing to resolve and nothing to start:
        // acknowledge the value just handled, and wait for whatever comes next.
        //
        // This is the one place an activity stops being a pure function of its input. The same
        // activity, entered twice with the same input, means "start a child" the first time and
        // "take the next value" afterwards — the difference is the subscription, which is carried
        // in the continuation envelope rather than recomputed.
        if (subscriptions.TryGetValue(run.ActivityName, out var subscribedChild))
        {
            await context.CallActivityAsync(nameof(AckChildOutputActivity),
                new AckChildOutputInput(subscribedChild));

            return await TakeChildOutputAsync(
                context, logger, run, logExecutionId, subscriptions, subscribedChild);
        }

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
            return ActivityOutcome.Completed(new Dictionary<string, object>
            {
                ["execution_id"] = childExecutionId.ToString()
            });
        }

        // Save child execution context (copies parent's bundle)
        await context.CallActivityAsync(nameof(SaveChildContextActivity),
            new SaveChildContextInput(input.ExecutionId,
                [new ChildContextSpec(childExecutionId, resolved.SubWorkflowKey!, resolved.WorkflowInput)]));

        // Whether this caller consumes what the child emits is settled here, once, from the
        // bundle — and travels with the child, because the child cannot ask at emit time who is
        // listening and still replay the same way twice.
        var consumes = run.Activity.Config is CallActivityConfig { ConsumesOutput: true };
        var outputEventName = consumes ? OutputEvents.For(run.ActivityName) : null;

        // The child inherits the limits but starts with its own empty visited set — a
        // sub-workflow invoked twice from a parent loop is two separate executions, not a
        // back-edge.
        var childTask = context.CallChildWorkflowAsync<object?>(
            nameof(WorkflowExecutor),
            new WorkflowExecutorInput(childExecutionId, resolved.SubWorkflowKey!,
                resolved.SubStartActivity!, input.ExecutionId,
                ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions,
                OutputEventName: outputEventName),
            new ChildWorkflowTaskOptions(childExecutionId.ToString()));

        if (consumes)
        {
            // Deliberately not awaited. This activity is left and re-entered once per emitted
            // value, and a task cannot survive that — it is a local, and a continue-as-new
            // restarts the method outright. So the child reports through events instead, and the
            // only thing that has to survive is the subscription below.
            subscriptions[run.ActivityName] = childExecutionId;

            return await TakeChildOutputAsync(
                context, logger, run, logExecutionId, subscriptions, childExecutionId);
        }

        var childOutput = await childTask;

        // The child's stream has served its only purpose. Reclaimed here rather than left to the
        // terminal cleanup because a parent looping over a sub-workflow call need never reach a
        // terminal state — and with no loop ceiling, "one stream per lap, forever" is a real
        // shape. The result above replays from orchestration history, not from the stream.
        await context.CallActivityAsync(nameof(DeleteOutputStreamActivity),
            new DeleteOutputStreamInput(childExecutionId));

        return ActivityOutcome.Completed(childOutput);
    }

    /// <summary>
    /// Stops every producer still waiting on this run.
    /// <para>
    /// A subscription lives only while the run keeps coming back to its call activity. Leaving
    /// that loop any other way ends it — an <c>on_emitted</c> handler that transitions to
    /// <c>end</c> rather than back, a failure elsewhere in the graph, a rule that returns a
    /// result. The child is parked on an acknowledgement at that moment, and nothing else in the
    /// system would ever release it: it is a child orchestration, so it is torn down if its
    /// caller is *cancelled*, but a caller that simply finishes leaves it waiting.
    /// </para>
    /// <para>
    /// Ordered by activity name because each cancellation is an activity call recorded in
    /// orchestration history, and a replay has to issue them in the same order it did the first
    /// time.
    /// </para>
    /// </summary>
    private static async Task AbandonSubscriptionsAsync(
        WorkflowContext context,
        ILogger logger,
        Dictionary<string, ExecutionId> subscriptions,
        ExecutionId logExecutionId)
    {
        if (subscriptions.Count == 0)
            return;

        foreach (var (activityName, childExecutionId) in
                 subscriptions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            logger.LogInformation(
                "Subscription at activity '{WorkflowActivity}' was abandoned; cancelling its " +
                "sub-workflow (execution {WorkflowExecutionId})",
                activityName, logExecutionId);

            await context.CallActivityAsync(nameof(CancelSubscribedChildActivity),
                new CancelSubscribedChildInput(childExecutionId));
        }

        subscriptions.Clear();
    }

    /// <summary>
    /// Waits for the child's next message: another value, or the one terminal message that ends
    /// the subscription.
    /// </summary>
    private static async Task<ActivityOutcome> TakeChildOutputAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowStep.Run run,
        ExecutionId logExecutionId,
        Dictionary<string, ExecutionId> subscriptions,
        ExecutionId childExecutionId)
    {
        var message = await context.WaitForExternalEventAsync<ChildOutputMessage>(
            OutputEvents.For(run.ActivityName));

        if (message.Kind == ChildOutputKind.Value)
        {
            logger.LogDebug(
                "Consuming an emitted value from '{WorkflowActivity}' (execution {WorkflowExecutionId})",
                run.ActivityName, logExecutionId);

            // Back to the loop, which routes this through on_emitted. The child stays parked on
            // its acknowledgement until this activity is re-entered, which is what holds the
            // unconsumed buffer at one entry.
            return ActivityOutcome.Emitted(message.Value);
        }

        // Terminal. The subscription is over, so re-entering this activity would start a fresh
        // child rather than resume a finished one.
        subscriptions.Remove(run.ActivityName);

        await context.CallActivityAsync(nameof(DeleteOutputStreamActivity),
            new DeleteOutputStreamInput(childExecutionId));

        if (message.Kind == ChildOutputKind.Error)
            throw new InvalidOperationException(
                $"Sub-workflow at activity '{run.ActivityName}' failed: {message.ErrorMessage}");

        return ActivityOutcome.Completed(message.Value);
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
