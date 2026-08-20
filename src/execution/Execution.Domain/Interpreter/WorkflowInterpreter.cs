using Common.Workflow;
using Execution.Domain.Templating;

namespace Execution.Domain.Interpreter;

/// <summary>
/// The decisions that need to read a workflow's templates: which transition an activity's
/// outcome selects, what an activity's configuration resolves to, and which promise branches
/// actually run.
///
/// Pure in the sense that matters — no store, no clock, no network, no orchestration
/// runtime. Given the same session and the same activity outcome it always decides the same
/// thing, so it can be exercised directly in a unit test.
/// </summary>
public sealed class WorkflowInterpreter(ITemplateEngine templateEngine, StructRenderer structRenderer)
{
    /// <summary>
    /// Where <paramref name="activityName"/> sends the execution, given what it produced.
    /// Rules are evaluated in order and the first whose condition matches wins.
    /// </summary>
    /// <param name="outcome">
    /// Which rule list applies. Only the driver knows: an emitted value and a returned result are
    /// identical by value, and a failure carries none at all.
    /// </param>
    /// <param name="response">
    /// The HTTP response this activity produced, when it was an HTTP activity. Surfaced as
    /// <c>{{ response.x }}</c> on every path, so a rule can branch on a status code.
    /// </param>
    /// <param name="error">
    /// Why the activity failed, surfaced as <c>{{ error.x }}</c>. Set only when
    /// <paramref name="outcome"/> is <see cref="ActivityOutcomeKind.Failed"/>.
    /// </param>
    /// <param name="dispatchedBy">
    /// The call activity whose <c>on_emitted</c> handler this path is running, when it is running
    /// one. Only the driver knows — a handler activity is an ordinary peer in a flat graph, so
    /// nothing about <paramref name="activityName"/> reveals how control reached it.
    /// </param>
    public TransitionDecision Advance(
        WorkflowSession session,
        string activityName,
        IReadOnlyDictionary<string, object>? activityInput,
        object? activityOutput,
        ActivityOutcomeKind outcome = ActivityOutcomeKind.Succeeded,
        HttpResponseContext? response = null,
        WorkflowErrorContext? error = null,
        string? dispatchedBy = null)
    {
        var activity = Require(session, activityName);
        var input = session.InputFor(activityInput);

        // A timer performs no work, so it has no output of its own — its input passes
        // through instead. That is what lets a back-edge keep threading accumulating loop
        // state across the wait. Derived from the activity definition rather than taken
        // from the caller so the two cannot disagree, and so a timer used as the start
        // activity picks up the run-input fallback above.
        var output = activity.Kind == ActivityKind.Timer
            ? input
            : activityOutput;

        // What a transition with no explicit input transform hands the next activity. On success
        // that is the output; on failure there is no output, and falling through to null would
        // make WorkflowSession.InputFor substitute the *run* input — silently rewinding a failure
        // handler to the state the workflow started in. The failing activity's own input is what
        // a handler actually needs to see.
        var passThrough = outcome == ActivityOutcomeKind.Failed ? input : output;

        // An emitted value is dispatched by the caller's on_emitted rules, which live on the call
        // configuration rather than on the activity — they only mean anything for an awaited
        // sub-workflow, so they cannot be authored anywhere they would be ignored.
        var rules = outcome switch
        {
            ActivityOutcomeKind.Emitted => (activity.Config as CallActivityConfig)?.OnEmitted ?? [],
            ActivityOutcomeKind.Failed => activity.OnFailure,
            _ => activity.OnSuccess
        };

        // Exhausting the rules means something different on each path — see Exhausted.
        if (rules.Count == 0)
            return Exhausted(outcome, activityName, dispatchedBy, passThrough);

        var scope = new TemplateScope()
            .WithInput(input)
            .WithOutput(output)
            .WithEnv(session.Env)
            .WithResponse(response)
            .WithError(error);

        foreach (var rule in rules)
        {
            // Check condition first — skip this rule if condition doesn't match
            if (!string.IsNullOrEmpty(rule.Condition))
            {
                var shouldTransition = templateEngine.Evaluate<bool>(rule.Condition, scope);
                if (!shouldTransition)
                    continue;
            }

            // Condition matched (or no condition) — execute the action
            return rule switch
            {
                { Transition: not null } => Goto(rule.Transition, scope, passThrough),

                { Result: not null } =>
                    new TransitionDecision.Return(structRenderer.Render(rule.Result, scope)),

                { Emit: not null } => Emit(rule.Emit, scope, passThrough, activityName),

                _ => throw new InvalidOperationException(
                    "Transition rule has no action set (neither transition, result nor emit)")
            };
        }

        // Every rule was evaluated and none matched — same meaning as having none at all.
        return Exhausted(outcome, activityName, dispatchedBy, passThrough);
    }

    /// <summary>
    /// What an unmatched rule list means. Not one answer but four, because "carry on as before"
    /// is a different sentence on each path:
    ///
    /// <list type="bullet">
    /// <item>A <b>failure</b> was never handled, so it keeps travelling outward — per
    /// <c>activity.proto</c>, "If no transitions match or list is empty, the error propagates".
    /// Checked first, and deliberately: a failure inside a handler must kill the run rather than
    /// quietly go back for the next value, which would swallow it.</item>
    /// <item>An <b>emission</b> nothing wanted is still an emission that was consumed, so the
    /// caller returns to its own call activity for the next one. Ending here instead is what made
    /// a filtering <c>on_emitted</c> — every condition missing on one uninteresting value —
    /// silently cancel the producer and report the run Completed.</item>
    /// <item>A <b>handler path that ran out of rules</b> is a handler that finished. Control
    /// returns to the call activity that dispatched it, the way a promise branch returns to its
    /// promise. This is what makes the back-edge optional rather than load-bearing.</item>
    /// <item>Anything else is an ordinary path with nowhere left to go: the run is done.</item>
    /// </list>
    ///
    /// The resumed target is reached with a plain <see cref="TransitionDecision.Goto"/>, so it is
    /// indistinguishable from the back-edge an author writes by hand — which is precisely why
    /// workflows that already write one keep behaving exactly as they did.
    /// </summary>
    private static TransitionDecision Exhausted(
        ActivityOutcomeKind outcome,
        string activityName,
        string? dispatchedBy,
        object? passThrough)
    {
        if (outcome == ActivityOutcomeKind.Failed)
            return new TransitionDecision.Propagate();

        // The call activity's own handler rules matched nothing: go back to it for the next value.
        if (outcome == ActivityOutcomeKind.Emitted)
            return Resume(activityName, passThrough);

        return dispatchedBy != null
            ? Resume(dispatchedBy, passThrough)
            : new TransitionDecision.End();
    }

    /// <summary>
    /// Back to <paramref name="callActivityName"/> to take the next emitted value. The input is
    /// carried for symmetry with every other transition and is not read: re-entering a live
    /// subscription acknowledges and waits rather than resolving the activity's configuration.
    /// </summary>
    private static TransitionDecision Resume(string callActivityName, object? passThrough)
    {
        return new TransitionDecision.Goto(
            callActivityName, passThrough as Dictionary<string, object>);
    }

    /// <summary>
    /// Renders <paramref name="activityName"/>'s configuration against the current scope.
    /// </summary>
    public ResolvedActivityInput ResolveInput(
        WorkflowSession session,
        string activityName,
        IReadOnlyDictionary<string, object>? activityInput)
    {
        var activity = Require(session, activityName);
        var input = session.InputFor(activityInput);

        var scope = new TemplateScope()
            .WithInput(input)
            .WithEnv(session.Env);

        return activity.Config switch
        {
            HttpActivityConfig http => ResolveHttp(http, scope, activityName),

            SubWorkflowActivityConfig subWorkflow =>
                ResolveSubWorkflow(subWorkflow, scope, activityInput),

            _ => throw new NotSupportedException(
                $"Unsupported activity config type '{activity.Kind}' for activity '{activityName}'")
        };
    }

    /// <summary>
    /// The branches a promise activity should start: conditions applied, <c>for_each</c>
    /// expanded, and each branch's input rendered.
    /// </summary>
    public PromiseExpansion ExpandPromise(
        WorkflowSession session,
        string activityName,
        IReadOnlyDictionary<string, object>? activityInput)
    {
        var activity = Require(session, activityName);

        if (activity.Config is not PromiseActivityConfig promise)
            throw new InvalidOperationException(
                $"Activity '{activityName}' is not a promise activity.");

        var input = session.InputFor(activityInput);

        var scope = new TemplateScope()
            .WithInput(input)
            .WithEnv(session.Env);

        var branches = new List<PromiseBranchPlan>();

        foreach (var branch in promise.Branches)
        {
            // Evaluate condition if set — skip branch if false
            if (!string.IsNullOrEmpty(branch.Condition))
            {
                var shouldInclude = templateEngine.Evaluate<bool>(branch.Condition, scope);
                if (!shouldInclude)
                    continue;
            }

            if (branch.ForEach != null)
            {
                // Dynamic expansion: evaluate collection, iterate with alias
                var collection = templateEngine.Evaluate<IEnumerable<object>>(branch.ForEach.Collection, scope);

                var index = 0;
                foreach (var item in collection)
                {
                    var itemScope = scope.With(branch.ForEach.Alias, item);

                    branches.Add(PlanBranch(
                        session, $"{branch.Name}_{index}", branch.Target, itemScope, activityInput));

                    index++;
                }
            }
            else
            {
                // Static branch: single resolution
                branches.Add(PlanBranch(session, branch.Name, branch.Target, scope, activityInput));
            }
        }

        // No parse step: the mode arrived as a oneof case and was mapped to the enum at the wire
        // boundary, so an unknown mode cannot reach here.
        return new PromiseExpansion(promise.Mode, promise.RequiredCount, branches);
    }

    private TransitionDecision Goto(TransitionTarget target, TemplateScope scope, object? activityOutput)
    {
        return new TransitionDecision.Goto(target.Name, ResolveTransitionInput(target, scope, activityOutput));
    }

    /// <summary>
    /// The value to append and where the run goes once it is durable. The target is required —
    /// an emit is not a terminal action, so a rule that emits and goes nowhere is a dead end
    /// rather than a return. <c>UTOS-T004</c> rejects that at load; this is the backstop for a
    /// bundle that reached the interpreter without it.
    /// </summary>
    private TransitionDecision Emit(
        EmitAction emit, TemplateScope scope, object? activityOutput, string activityName)
    {
        var target = emit.Transition
                     ?? throw new InvalidOperationException(
                         $"Emit rule on activity '{activityName}' has no transition target.");

        return new TransitionDecision.Emit(
            structRenderer.Render(emit.Value ?? ValueMap.Empty, scope),
            target.Name,
            ResolveTransitionInput(target, scope, activityOutput));
    }

    /// <summary>
    /// Resolves the mapped input for the target activity.
    /// If the transition target has an explicit input transform, renders it.
    /// Otherwise, passes the raw activity output through as-is.
    /// </summary>
    private Dictionary<string, object>? ResolveTransitionInput(
        TransitionTarget target,
        TemplateScope scope,
        object? activityOutput)
    {
        if (target.Input != null)
            return structRenderer.Render(target.Input, scope);

        // Pass-through: raw output becomes the next activity's input
        return activityOutput as Dictionary<string, object>;
    }

    private ResolvedActivityInput ResolveHttp(
        HttpActivityConfig httpConfig,
        TemplateScope scope,
        string activityName)
    {
        var url = templateEngine.RenderToString(httpConfig.Url, scope);

        // A templated URL skips the load-time shape check, since its scheme or host may
        // come from {{ env.x }}. Re-apply the same rules now that it is rendered, so a bad
        // value fails with the same diagnostic instead of a raw HTTP client error.
        if (!HttpUrlRules.IsValidRendered(url))
            throw new InvalidOperationException(
                $"Activity '{activityName}' resolved to an unusable URL '{url}': " +
                $"{HttpUrlRules.GetError(url)}");

        var headers = new Dictionary<string, string>();
        foreach (var (key, valueTemplate) in httpConfig.HeaderMap)
            headers[key] = templateEngine.RenderToString(valueTemplate, scope);

        string? body = null;
        if (httpConfig.Method is "POST" or "PUT" && httpConfig.Body != null)
            body = templateEngine.RenderToString(httpConfig.Body, scope);

        return new ResolvedActivityInput.Http(url, httpConfig.Method, body, headers);
    }

    private ResolvedActivityInput ResolveSubWorkflow(
        SubWorkflowActivityConfig config,
        TemplateScope scope,
        IReadOnlyDictionary<string, object>? rawActivityInput)
    {
        var subInput = config.Input != null
            ? structRenderer.Render(config.Input, scope)
            : AsMutable(rawActivityInput);

        // The orchestrator still asks a yes/no question here — await the child or not — so the
        // mode collapses to a flag at this boundary rather than the shape being threaded further.
        return new ResolvedActivityInput.SubWorkflow(
            config.Workflow, config.StartActivity, subInput, config is SpawnActivityConfig);
    }

    private PromiseBranchPlan PlanBranch(
        WorkflowSession session,
        string branchName,
        TransitionTarget target,
        TemplateScope scope,
        IReadOnlyDictionary<string, object>? activityInput)
    {
        // The target names an activity in the current workflow. Which one it is decides only
        // *what* the branch runs, not whether it is allowed to.
        var targetActivity = session.Graph.Find(target.Name)
                             ?? throw new InvalidOperationException(
                                 $"Branch target activity '{target.Name}' not found in workflow");

        // Render branch input from target.Input if set, otherwise pass through activity input
        var branchInput = target.Input != null
            ? structRenderer.Render(target.Input, scope)
            : AsMutable(activityInput);

        // A workflow-kind target names the sub-workflow to run, and is a pointer rather than the
        // work itself. Any other target *is* the work: run this same workflow from that activity.
        //
        // That every branch is a child execution is how the driver fans out, not something the
        // spec asks an author to arrange. PromiseBranch.target requires only "which activity to
        // start", and no UTOS rule constrains its kind — so demanding a workflow.call here made
        // the daemon reject documents the shared validator accepts, which is the one divergence
        // the two are supposed to be incapable of.
        return targetActivity.Config is SubWorkflowActivityConfig config
            ? new PromiseBranchPlan(branchName, config.Workflow, config.StartActivity, branchInput)
            : new PromiseBranchPlan(branchName, session.Graph.WorkflowKey, target.Name, branchInput);
    }

    private static WorkflowActivity Require(WorkflowSession session, string activityName)
    {
        return session.Graph.Find(activityName)
               ?? throw new InvalidOperationException(
                   $"Activity '{activityName}' not found in workflow '{session.Graph.WorkflowKey}'");
    }

    /// <summary>
    /// Pass-through inputs travel onward as mutable state, so hand back the same instance
    /// when there is one rather than copying on every hop.
    /// </summary>
    private static Dictionary<string, object>? AsMutable(IReadOnlyDictionary<string, object>? input)
    {
        return input as Dictionary<string, object>;
    }
}
