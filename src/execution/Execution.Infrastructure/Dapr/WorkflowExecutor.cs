using Common.Constants;
using Common.SeedWork;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Aggregates;
using Execution.Domain.Interpreter;
using Execution.Infrastructure.Dapr.Activities;
using Execution.Infrastructure.Http;
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
    string? OutputEventName = null,
    // The run whose watch stream this execution logs into: the root of the call tree, carried
    // down unchanged. ParentExecutionId cannot do this job — it names the direct caller, which is
    // right for handing emitted values back and wrong for a grandchild's log lines.
    ExecutionId? RootExecutionId = null)
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
public readonly record struct ActivityOutcome(
    object? Output,
    bool IsEmission,
    HttpResponseContext? Response = null)
{
    public static ActivityOutcome Completed(object? output) => new(output, false);

    /// <summary>An HTTP activity, which additionally surfaces its response to templates.</summary>
    public static ActivityOutcome Completed(object? output, HttpResponseContext? response) =>
        new(output, false, response);

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
        var isChild = input.ParentExecutionId != null;
        // A child's lines carry which workflow wrote them. The root's do not: it is the run being
        // watched, so naming it on every line would repeat the one thing the reader already knows.
        var logger = WorkflowSourceLogger.For(
            context.CreateReplaySafeLogger<WorkflowExecutor>(), isChild ? input.WorkflowKey : null);
        // A non-null Visited set means this instance resumed via continue-as-new rather than
        // being scheduled fresh, so the run's opening bookkeeping must not repeat.
        var isContinuation = input.Visited != null;
        var startTime = input.StartedAt ?? context.CurrentUtcDateTime;
        // Every execution in a call tree logs under the root's id, so the one stream a client
        // watches carries the whole run, however deep it nests. ParentExecutionId alone reached one
        // level: a grandchild's lines routed to its direct parent — a stream nobody watches. It
        // remains the fallback for a run already in flight when RootExecutionId was introduced.
        var logExecutionId = input.RootExecutionId ?? input.ParentExecutionId ?? input.ExecutionId;

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


            // The loop runs until a transition names no next activity: the rules ran out, or a
            // rule returned. Ending is the absence of a target, never a target — there is no
            // `end` keyword to recognise, and the failing case below is an explicit result too.
            while (currentActivityName != null)
            {
                var step = WorkflowNavigator.Next(graph, currentActivityName, cursor, limits);

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

                ActivityOutcome outcome;
                var outcomeKind = ActivityOutcomeKind.Succeeded;
                WorkflowErrorContext? activityError = null;
                ExceptionDispatchInfo? unhandledFailure = null;

                try
                {
                    outcome = await PerformAsync(
                        context, logger, input, run, currentActivityInput, logExecutionId, subscriptions);

                    outcomeKind = outcome.IsEmission
                        ? ActivityOutcomeKind.Emitted
                        : ActivityOutcomeKind.Succeeded;
                }
                // Filtered on the activity actually declaring on_failure rules, so a workflow that
                // does not opt in keeps exactly today's path — the exception is never caught, and
                // no orchestration-internal control flow can be swallowed by this block. When the
                // filter is false the stack is not even unwound.
                catch (Exception activityEx) when (run.Activity.OnFailure.Count > 0)
                {
                    logger.LogInformation(
                        "Activity '{WorkflowActivity}' failed and has on_failure rules: {ErrorMessage} (execution {WorkflowExecutionId})",
                        run.ActivityName, activityEx.Message, logExecutionId);

                    unhandledFailure = ExceptionDispatchInfo.Capture(activityEx);
                    outcomeKind = ActivityOutcomeKind.Failed;

                    // What `error.code` is in the rule about to run. A sub-workflow that failed by
                    // an `error` action arrives as a WorkflowFailedException carrying the author's
                    // code and details, and those are what the rule matches on; anything else the
                    // daemon raised itself, and reports the generic code — a rule branches on
                    // response.status for those, which is the part that carries real detail.
                    activityError = activityEx is WorkflowFailedException authored
                        ? new WorkflowErrorContext(authored.Code, authored.Message, authored.Details)
                        : new WorkflowErrorContext(WorkflowErrorCode.ExecutionFailed, activityEx.Message);

                    // A failed HTTP activity still has a response, and it is the whole reason
                    // on_failure can tell a 429 from a 401.
                    outcome = activityEx is HttpActivityFailedException httpFailure
                        ? new ActivityOutcome(null, false, httpFailure.ToResponseContext())
                        : default;
                }

                // An emission routes through on_emitted rather than on_success: the child has
                // produced a value, not finished. Which rule list applies is the interpreter's
                // call; that it was an emission — or a failure — is only knowable here.
                var transition = await context.CallActivityAsync<EvaluateTransitionOutput>(
                    nameof(EvaluateTransitionActivity),
                    new EvaluateTransitionInput(input.ExecutionId, input.WorkflowKey,
                        run.ActivityName, currentActivityInput, outcome.Output, outcomeKind,
                        outcome.Response, activityError));

                // Which rule decided where the run goes next. Logged before the rethrow below, so a
                // failure routed to `error` still says which rule sent it there. "rule 1 of 3" is
                // the useful part: it shows the first match won and the rest were never evaluated.
                if (transition.MatchedList != null)
                    logger.LogInformation(
                        "Activity '{WorkflowActivity}' {TransitionList} rule {RuleNumber} of {RuleCount} matched → {TransitionTarget} (execution {WorkflowExecutionId})",
                        run.ActivityName, transition.MatchedList, transition.MatchedRule, transition.RuleCount,
                        DescribeTarget(transition), logExecutionId);

                // No rule matched the failure, so it was never handled — rethrow it as it was,
                // rather than letting the run fall through to a normal completion.
                if (transition.Unhandled)
                    unhandledFailure!.Throw();

                // A rule said fail, and said why: the run fails with exactly what the author
                // wrote, on either path. On the failure path that replaces the cause being
                // handled — which is the point of the action; an author who wants the cause
                // reported writes `message: "{{ error.message }}"`, and one who wants it reported
                // unchanged declares no rule at all. The subscription, if any, was already left.
                if (transition.Error is { } raised)
                    throw new WorkflowFailedException(raised.Code, raised.Message, raised.Details);

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

                // The consumer said it is done consuming. Ending the subscription and cancelling
                // the producer are one event, and it happens here rather than in the sweep below,
                // because a consumer that transitions away carries on running — possibly for a
                // very long time — and leaving the producer parked for the duration would be a
                // leak, not a delay. Before the transition is taken, so nothing this run does next
                // can race the cancellation.
                if (transition.LeavesSubscription)
                    await LeaveSubscriptionAsync(
                        context, logger, subscriptions, run.ActivityName, logExecutionId);

                if (transition.ReturnValue != null)
                {
                    workflowOutput = transition.ReturnValue;
                    break;
                }

                // A handler for the value just consumed. It is a document, so this runs it as a
                // child execution to termination and then comes straight back to the call
                // activity for the next value — the handler decides nothing about where this
                // workflow goes, which is the whole point of it being a document.
                if (transition.Dispatch != null)
                {
                    await RunHandlerAsync(context, logger, input, run.ActivityName,
                        transition.Dispatch, logExecutionId);

                    currentActivityName = run.ActivityName;
                    currentActivityInput = null;
                    continue;
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
            // The three fields every report of this failure carries. Authored by a rule's `error`
            // action — here or in a child this run was handling — they are the author's; raised by
            // the daemon, the code is the generic one and there are no details.
            var (failureCode, failureDetails) = ex is WorkflowFailedException authored
                ? (authored.Code, authored.Details)
                : (WorkflowErrorCode.ExecutionFailed, null);

            logger.LogError(
                "Workflow execution failed [{FailureCode}]: {ErrorMessage} (execution {WorkflowExecutionId})",
                failureCode, ex.Message, logExecutionId);

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
                        new ChildOutputMessage(ChildOutputKind.Error, null, ex.Message,
                            failureCode, failureDetails)));

            // A failure abandons a subscription just as surely as reaching the end does — more
            // so, since the failure may be the very thing that stopped the run returning to its
            // call activity.
            await AbandonSubscriptionsAsync(context, logger, subscriptions, logExecutionId);

            await context.CallActivityAsync(nameof(AppendOutputEntryActivity),
                new AppendOutputEntryInput(input.ExecutionId, OutputEntryKind.Error,
                    ErrorCode: failureCode, ErrorMessage: ex.Message, ErrorDetails: failureDetails));

            if (!isChild)
                await context.CallActivityAsync(nameof(WriteLifecycleEventActivity),
                    new WriteLifecycleEventInput(input.ExecutionId, ExecutionLifecycleTransition.Failed,
                        ex.Message, failureCode, failureDetails));

            throw;
        }
    }

    /// <summary>Where a matched rule sends the run, as a log reads it.</summary>
    private static string DescribeTarget(EvaluateTransitionOutput transition) =>
        transition.Dispatch != null ? $"handler {transition.Dispatch.Workflow}"
        : transition.Error != null ? $"error {transition.Error.Code}"
        : transition.ReturnValue != null ? "result"
        : transition.NextActivityName ?? "end of path";

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
                return await CallHttpAsync(context, logger, input, run, activityInput, logExecutionId);

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

    private static async Task<ActivityOutcome> CallHttpAsync(
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
                    resolved.Url, resolved.HttpMethod, resolved.Body, resolved.Headers,
                    LogSource: input.ParentExecutionId is null ? null : input.WorkflowKey));

            var activityDuration = (context.CurrentUtcDateTime - activityStartTime).TotalMilliseconds;

            // The handler reports a non-2xx rather than throwing, so that the status and headers
            // survive the Dapr boundary. Turning that into a failure is this side's decision.
            if (!result.Success)
                throw new HttpActivityFailedException(
                    run.ActivityName, result.StatusCode, result.Headers, result.BodyText, result.Body);

            logger.LogInformation(
                "Activity '{WorkflowActivity}' completed in {DurationMs}ms (execution {WorkflowExecutionId})",
                run.ActivityName, (long)activityDuration, logExecutionId);

            return ActivityOutcome.Completed(
                result.Output,
                new HttpResponseContext(result.StatusCode, result.Headers, result.BodyText, result.Body));
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

        // Resolve workflow call input via central template rendering
        var resolved = await context.CallActivityAsync<ResolveActivityInputOutput>(
            nameof(ResolveActivityInputActivity),
            new ResolveActivityInputInput(input.ExecutionId, input.WorkflowKey,
                run.ActivityName, activityInput));

        // NewGuid is the replay-safe source, so the same child id comes back on replay.
        var childExecutionId = new ExecutionId(context.NewGuid());

        // The line that makes the rest of the tree legible. It names the child by canonical
        // identity, which is what the child's own lines carry as their source, so the reader can
        // join the two; and by its execution id — once, here — because that is the handle a reader
        // can act on with utos logs or utos cancel. The trailing root id is for routing and is
        // dropped from the stream; the child's id is not, since it names a different execution.
        if (resolved.Detached)
            logger.LogInformation(
                "Activity '{WorkflowActivity}' spawns {SubWorkflow} as execution {ChildExecutionId} (execution {WorkflowExecutionId})",
                run.ActivityName, resolved.SubWorkflowKey, childExecutionId, logExecutionId);
        else
            logger.LogInformation(
                "Activity '{WorkflowActivity}' calls {SubWorkflow} as execution {ChildExecutionId} (execution {WorkflowExecutionId})",
                run.ActivityName, resolved.SubWorkflowKey, childExecutionId, logExecutionId);

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
        // back-edge. Its dispatch stack starts empty for the same reason: the caller's handlers
        // are the caller's, and a child running out of rules ends the child.
        var childTask = context.CallChildWorkflowAsync<object?>(
            nameof(WorkflowExecutor),
            new WorkflowExecutorInput(childExecutionId, resolved.SubWorkflowKey!,
                resolved.SubStartActivity!, input.ExecutionId,
                ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions,
                OutputEventName: outputEventName,
                RootExecutionId: logExecutionId),
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

        object? childOutput;
        try
        {
            childOutput = await childTask;
        }
        // The SDK's own type, and by name for the runtime's underlying TaskFailedException, which
        // has surfaced directly in some SDK versions: either way the child's failure arrived
        // flattened to a message. Its code and details are on the child's stream, written before
        // the child reported — read them back so this run fails, or handles the failure, with
        // what the author wrote.
        catch (Exception ex) when (ex is WorkflowTaskFailedException
                                   || ex.GetType().Name.EndsWith("TaskFailedException", StringComparison.Ordinal))
        {
            throw await ReadChildFailureAsync(context, childExecutionId);
        }

        // The child's stream has served its only purpose. Reclaimed here rather than left to the
        // terminal cleanup because a parent looping over a sub-workflow call need never reach a
        // terminal state — and with no loop ceiling, "one stream per lap, forever" is a real
        // shape. The result above replays from orchestration history, not from the stream.
        await context.CallActivityAsync(nameof(DeleteOutputStreamActivity),
            new DeleteOutputStreamInput(childExecutionId));

        return ActivityOutcome.Completed(childOutput);
    }

    /// <summary>
    /// The failure an awaited child recorded, as the exception this run fails with. The stream is
    /// left in place: it is the record of why, and the terminal cleanup reclaims it.
    /// </summary>
    private static async Task<WorkflowFailedException> ReadChildFailureAsync(
        WorkflowContext context, ExecutionId childExecutionId)
    {
        var failure = await context.CallActivityAsync<ReadChildFailureOutput>(
            nameof(ReadChildFailureActivity), new ReadChildFailureInput(childExecutionId));

        return new WorkflowFailedException(failure.Code, failure.Message, failure.Details);
    }

    /// <summary>
    /// Runs one <c>on_emitted</c> handler to termination, relaying anything it emits.
    /// <para>
    /// The handler is a document, so it is a child execution like any other — but a *consuming*
    /// one, which is what makes relaying fall out rather than needing a rule. It is told to report
    /// on <see cref="OutputEvents.ForHandler"/>, so each value it emits arrives here, is appended
    /// to <em>this</em> execution's stream and handed to <em>this</em> execution's caller. A value
    /// emitted inside a handler therefore comes out of the consumer, exactly as it did when the
    /// handler ran inline — the three-level relay depends on that and would otherwise have broken
    /// silently when the handler moved into its own execution.
    /// </para>
    /// <para>
    /// Every wait here is on something further up the chain — our caller acknowledges us before we
    /// acknowledge the handler, and only then does the producer get its acknowledgement. The waits
    /// form a chain rather than a cycle, which is why this cannot deadlock however deep the
    /// relay goes.
    /// </para>
    /// <para>
    /// The handler's terminal result is discarded. A handler has one way to say something and it
    /// is to emit; a result would be a second, and would have to mean something different from
    /// the emission it sits beside.
    /// </para>
    /// </summary>
    private static async Task RunHandlerAsync(
        WorkflowContext context,
        ILogger logger,
        WorkflowExecutorInput input,
        string callActivityName,
        DispatchTarget dispatch,
        ExecutionId logExecutionId)
    {
        // Replay-safe, and unique across a continuation: the orchestrator seeds its guid namespace
        // from the execution id, which ContinueAsNew mints fresh. That was only true from Dapr
        // 1.18.4 — before it, NewGuid restarted with the history and re-minted the id the live
        // producer child already held, which Dapr then dropped silently and hung the run. See the
        // Requirements section of CHANGELOG.md.
        var handlerExecutionId = new ExecutionId(context.NewGuid());

        logger.LogInformation(
            "Dispatching '{HandlerWorkflow}' from '{HandlerStartActivity}' as {HandlerExecutionId} "
            + "to handle a value from '{WorkflowActivity}' (execution {WorkflowExecutionId})",
            dispatch.Workflow, dispatch.StartActivity, handlerExecutionId, callActivityName,
            logExecutionId);

        await context.CallActivityAsync(nameof(SaveChildContextActivity),
            new SaveChildContextInput(input.ExecutionId,
                [new ChildContextSpec(handlerExecutionId, dispatch.Workflow, dispatch.Input)]));

        var handlerEventName = OutputEvents.ForHandler(callActivityName);

        // Deliberately not awaited, for the same reason the producer's task is not: the handler
        // reports through events, and this method may be re-entered after a continue-as-new that
        // no task survives.
        _ = context.CallChildWorkflowAsync<object?>(
            nameof(WorkflowExecutor),
            new WorkflowExecutorInput(handlerExecutionId, dispatch.Workflow,
                dispatch.StartActivity, input.ExecutionId,
                ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions,
                OutputEventName: handlerEventName,
                RootExecutionId: logExecutionId),
            new ChildWorkflowTaskOptions(handlerExecutionId.ToString()));

        while (true)
        {
            var message = await context.WaitForExternalEventAsync<ChildOutputMessage>(handlerEventName);

            if (message.Kind != ChildOutputKind.Value)
            {
                // Terminal either way. The handler's own failure is the consumer's failure: it was
                // dispatched to process this value, and swallowing that would drop the value and
                // carry on as if it had been handled.
                await context.CallActivityAsync(nameof(DeleteOutputStreamActivity),
                    new DeleteOutputStreamInput(handlerExecutionId));

                // Rethrown as what the handler failed with, code and details intact, so a rule
                // on this consumer can match an error the handler raised on purpose.
                if (message.Kind == ChildOutputKind.Error)
                {
                    logger.LogError(
                        "Emission handler '{HandlerWorkflow}' dispatched from activity '{WorkflowActivity}' "
                        + "failed [{FailureCode}]: {ErrorMessage} (execution {WorkflowExecutionId})",
                        dispatch.Workflow, callActivityName, message.ErrorCode ?? WorkflowErrorCode.ExecutionFailed,
                        message.ErrorMessage, logExecutionId);

                    throw AsFailure(message);
                }

                return;
            }

            await context.CallActivityAsync(nameof(AppendOutputEntryActivity),
                new AppendOutputEntryInput(input.ExecutionId, OutputEntryKind.Value, message.Value));

            if (input.OutputEventName != null)
            {
                await context.CallActivityAsync(nameof(RaiseChildOutputActivity),
                    new RaiseChildOutputInput(
                        input.ParentExecutionId!.Value, input.OutputEventName,
                        new ChildOutputMessage(ChildOutputKind.Value, message.Value)));

                await context.WaitForExternalEventAsync<bool>(OutputEvents.Consumed);
            }

            await context.CallActivityAsync(nameof(AckChildOutputActivity),
                new AckChildOutputInput(handlerExecutionId));
        }
    }

    /// <summary>
    /// Ends one subscription deliberately: the consumer's rule said <c>transition</c> or
    /// <c>result</c>, so no further value will be observed and the producer is stopped now.
    /// <para>
    /// The stream is left in place rather than deleted, unlike the terminal path in
    /// <see cref="TakeChildOutputAsync"/>. There the producer finished and its stream is spent;
    /// here it is being cut short mid-run, and what it emitted stays readable — the consumer
    /// stopping early is not a reason to destroy the record of why.
    /// </para>
    /// </summary>
    private static async Task LeaveSubscriptionAsync(
        WorkflowContext context,
        ILogger logger,
        Dictionary<string, ExecutionId> subscriptions,
        string activityName,
        ExecutionId logExecutionId)
    {
        // Absent when the rule fired on the terminal value rather than an emitted one, which
        // TakeChildOutputAsync has already unsubscribed. Nothing to cancel: the producer is done.
        if (!subscriptions.Remove(activityName, out var childExecutionId))
            return;

        logger.LogInformation(
            "Consumer left the subscription at activity '{WorkflowActivity}'; cancelling its " +
            "sub-workflow (execution {WorkflowExecutionId})",
            activityName, logExecutionId);

        await context.CallActivityAsync(nameof(CancelChildExecutionActivity),
            new CancelChildExecutionInput(childExecutionId));
    }

    /// <summary>
    /// Stops every producer still waiting on this run.
    /// <para>
    /// A subscription lives only while the run keeps coming back to its call activity. Leaving
    /// that loop any other way ends it — a failure elsewhere in the graph, or simply reaching the
    /// end of the workflow. The child is parked on an acknowledgement at that moment, and nothing
    /// else in the system would ever release it: it is a child orchestration, so it is torn down
    /// if its caller is *cancelled*, but a caller that simply finishes leaves it waiting.
    /// </para>
    /// <para>
    /// This is the involuntary counterpart to <see cref="LeaveSubscriptionAsync"/>, and by the
    /// time a run gets here that one has usually already emptied the map. What is left is what no
    /// rule accounted for.
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

            await context.CallActivityAsync(nameof(CancelChildExecutionActivity),
                new CancelChildExecutionInput(childExecutionId));
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
        {
            logger.LogError(
                "Sub-workflow at activity '{WorkflowActivity}' failed [{FailureCode}]: {ErrorMessage} (execution {WorkflowExecutionId})",
                run.ActivityName, message.ErrorCode ?? WorkflowErrorCode.ExecutionFailed, message.ErrorMessage,
                logExecutionId);

            throw AsFailure(message);
        }

        return ActivityOutcome.Completed(message.Value);
    }

    /// <summary>
    /// A child's terminal error message as the exception this run fails with: the author's code
    /// and details when the child failed by an <c>error</c> action, the generic code otherwise.
    /// </summary>
    private static WorkflowFailedException AsFailure(ChildOutputMessage message) =>
        new(message.ErrorCode ?? WorkflowErrorCode.ExecutionFailed,
            message.ErrorMessage ?? string.Empty,
            message.ErrorDetails);

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

        // 4. Fan out — launch all branches as child workflows. The ids are kept alongside the
        //    tasks because settling has to be able to *name* the branches it did not wait for,
        //    and a task is not an address.
        var branchTasks = new Dictionary<string, Task<object?>>(StringComparer.Ordinal);
        var branchExecutionIds = new Dictionary<string, ExecutionId>(StringComparer.Ordinal);
        foreach (var (branch, branchExecutionId) in branches)
        {
            branchExecutionIds[branch.BranchName] = branchExecutionId;

            branchTasks[branch.BranchName] = context.CallChildWorkflowAsync<object?>(
                nameof(WorkflowExecutor),
                new WorkflowExecutorInput(branchExecutionId, branch.WorkflowKey,
                    branch.StartActivity, input.ExecutionId,
                    ContinueAsNewAfterTransitions: input.ContinueAsNewAfterTransitions,
                    RootExecutionId: logExecutionId),
                new ChildWorkflowTaskOptions(branchExecutionId.ToString()));
        }

        // 5. Settle by mode, stopping whatever the mode turned out not to need
        var promiseOutput = await SettleAsync(context, logger, run.ActivityName, logExecutionId,
            resolved.Mode, resolved.RequiredCount, branchTasks, branchExecutionIds);

        logger.LogInformation(
            "Promise activity '{WorkflowActivity}' resolved ({PromiseMode} mode, {BranchCount} branches) (execution {WorkflowExecutionId})",
            run.ActivityName, ((PromiseActivityConfig)run.Activity.Config).Mode, resolved.Branches.Count, logExecutionId);

        return promiseOutput;
    }

    /// <summary>
    /// Awaits branches one settlement at a time, letting <see cref="PromiseResolver"/> say
    /// when the mode is satisfied. The awaiting is this layer's; which branches decide the
    /// outcome is not.
    /// <para>
    /// Whatever is still running when the mode is satisfied is cancelled before the promise
    /// returns, on every path — a <c>race</c> loser, an <c>any</c> also-ran, the siblings of the
    /// branch whose failure sank an <c>all</c>. Dropping their tasks, which is all this used to
    /// do, stops nobody: the branch is a live child orchestration and carries on issuing requests
    /// and writing entries whether or not its result can still be read.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, object?>> SettleAsync(
        WorkflowContext context,
        ILogger logger,
        string activityName,
        ExecutionId logExecutionId,
        PromiseMode mode,
        int requiredCount,
        Dictionary<string, Task<object?>> branchTasks,
        IReadOnlyDictionary<string, ExecutionId> branchExecutionIds)
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

        // Before the switch, so it happens on the failure paths too, and before anything is
        // thrown.
        await CancelOutstandingBranchesAsync(
            context, logger, activityName, logExecutionId, remaining.Keys, branchExecutionIds);

        switch (progress)
        {
            case PromiseProgress.Resolved resolved:
                return resolved.Results;

            case PromiseProgress.BranchFailed branchFailed:
                // The branch's own error is the useful one, and it is on the branch's stream
                // with the code and details the runtime's exception lost — see
                // ReadChildFailureActivity. The branch name goes to the log; the failure itself
                // is reported as the branch authored it.
                logger.LogError(
                    "Promise activity '{WorkflowActivity}' failed: branch '{PromiseBranch}' failed (execution {WorkflowExecutionId})",
                    activityName, branchFailed.BranchName, logExecutionId);
                throw await ReadChildFailureAsync(context, branchExecutionIds[branchFailed.BranchName]);

            case PromiseProgress.Failed failed:
                throw new InvalidOperationException(failed.Reason);

            default:
                throw new InvalidOperationException(
                    $"Promise did not settle (mode: {mode}, {branchTasks.Count} branches)");
        }
    }

    /// <summary>
    /// Stops the branches a settled promise is no longer waiting on.
    /// <para>
    /// Ordered explicitly, for the same reason <see cref="AbandonSubscriptionsAsync"/> is: each
    /// cancellation is a history-recorded activity call, and a dictionary's iteration order is
    /// not a promise, so an unordered sweep would replay in a different order than it ran.
    /// </para>
    /// </summary>
    private static async Task CancelOutstandingBranchesAsync(
        WorkflowContext context,
        ILogger logger,
        string activityName,
        ExecutionId logExecutionId,
        IEnumerable<string> branchNames,
        IReadOnlyDictionary<string, ExecutionId> branchExecutionIds)
    {
        foreach (var branchName in branchNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            logger.LogInformation(
                "Promise activity '{WorkflowActivity}' settled without branch '{PromiseBranch}'; " +
                "cancelling it (execution {WorkflowExecutionId})",
                activityName, branchName, logExecutionId);

            await context.CallActivityAsync(nameof(CancelChildExecutionActivity),
                new CancelChildExecutionInput(branchExecutionIds[branchName]));
        }
    }
}
