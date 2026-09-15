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
    public TransitionDecision Advance(
        WorkflowSession session,
        string activityName,
        IReadOnlyDictionary<string, object>? activityInput,
        object? activityOutput,
        ActivityOutcomeKind outcome = ActivityOutcomeKind.Succeeded,
        HttpResponseContext? response = null,
        WorkflowErrorContext? error = null)
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

        var scope = new TemplateScope()
            .WithInput(input)
            .WithOutput(output)
            .WithEnv(session.Env)
            .WithResponse(response)
            .WithError(error);

        // An emitted value is handled by the caller's on_emitted rules, which live on the call
        // configuration rather than on the activity — they only mean anything for an awaited
        // sub-workflow, so they cannot be authored anywhere they would be ignored. They are
        // dispatches, not transitions, so this path is answered separately rather than being
        // folded into the loop below.
        if (outcome == ActivityOutcomeKind.Emitted)
            return DispatchEmitted(
                (activity.Config as CallActivityConfig)?.OnEmitted ?? [],
                scope, activityName, passThrough);

        var rules = outcome == ActivityOutcomeKind.Failed ? activity.OnFailure : activity.OnSuccess;

        // Exhausting the rules means something different on each path — see Exhausted.
        if (rules.Count == 0)
            return Exhausted(outcome, activityName, passThrough);

        var list = outcome == ActivityOutcomeKind.Failed ? "onFailure" : "onSuccess";
        var number = 0;
        foreach (var rule in rules)
        {
            number++;

            // Check condition first — skip this rule if condition doesn't match
            if (!string.IsNullOrEmpty(rule.Condition)
                && !templateEngine.EvaluateCondition(rule.Condition, scope))
                continue;

            // Condition matched (or no condition) — execute the action
            TransitionDecision decision = rule switch
            {
                { Transition: not null } => Goto(rule.Transition, scope, passThrough),

                { Result: not null } =>
                    new TransitionDecision.Return(structRenderer.Render(rule.Result, scope)),

                { Emit: not null } => Emit(rule.Emit, scope, passThrough, activityName),

                _ => throw new InvalidOperationException(
                    "Transition rule has no action set (neither transition, result nor emit)")
            };

            return decision with { Match = new RuleMatch(list, number, rules.Count) };
        }

        // Every rule was evaluated and none matched — same meaning as having none at all.
        return Exhausted(outcome, activityName, passThrough);
    }

    /// <summary>
    /// What this emitted value does to the run: hand it to a document and take the next one, or
    /// stop consuming — by continuing somewhere in this workflow, or by ending with a result.
    /// <para>
    /// An unmatched list is the case that made a *filtering* consumer unwritable: every condition
    /// missing on one uninteresting value used to end the run. It means "skip this value", so the
    /// answer is to go back to the call activity for the next one — the same answer an empty list
    /// gives, which is why they share a return rather than being separately special-cased.
    /// </para>
    /// <para>
    /// The two leaving actions are the consumer's own, evaluated here, in the consuming execution.
    /// That is what makes them safe where the same words inside a dispatched handler would not be:
    /// a handler runs in an execution holding no subscription, so a transition there could only
    /// re-enter the call activity and start a *second* producer.
    /// </para>
    /// </summary>
    private TransitionDecision DispatchEmitted(
        IReadOnlyList<EmissionRule> rules,
        TemplateScope scope,
        string callActivityName,
        object? passThrough)
    {
        var number = 0;
        foreach (var rule in rules)
        {
            number++;

            if (!string.IsNullOrEmpty(rule.Condition)
                && !templateEngine.EvaluateCondition(rule.Condition, scope))
                continue;

            TransitionDecision decision = rule switch
            {
                { Handle: not null } => new TransitionDecision.Dispatch(
                    rule.Handle.Workflow,
                    rule.Handle.StartActivity,
                    rule.Handle.Input != null
                        ? structRenderer.Render(rule.Handle.Input, scope)
                        : passThrough as Dictionary<string, object>),

                { Transition: not null } => Goto(rule.Transition, scope, passThrough),

                { Result: not null } =>
                    new TransitionDecision.Return(structRenderer.Render(rule.Result, scope)),

                _ => throw new InvalidOperationException(
                    "Emission rule has no action set (neither handle, transition nor result)")
            };

            return decision with { Match = new RuleMatch("onEmitted", number, rules.Count) };
        }

        return Resume(callActivityName, passThrough);
    }

    /// <summary>
    /// What an unmatched rule list means. Two answers, because "carry on as before" is a
    /// different sentence on each path:
    ///
    /// <list type="bullet">
    /// <item>A <b>failure</b> was never handled, so it keeps travelling outward — per
    /// <c>activity.proto</c>, "If no transitions match or list is empty, the error propagates".</item>
    /// <item>Anything else is an ordinary path with nowhere left to go: the run is done.</item>
    /// </list>
    ///
    /// It used to have a third answer, for a path that had run out of rules *inside* a handler,
    /// which meant one iteration was finished rather than the run. That distinction is gone with
    /// the handler: it is a document now, so its own path ending ends its own execution and
    /// nothing else, and the consumer learns of it by the child terminating. The emitted path
    /// never reaches here at all — see <see cref="DispatchEmitted"/>.
    /// </summary>
    private static TransitionDecision Exhausted(
        ActivityOutcomeKind outcome,
        string activityName,
        object? passThrough)
    {
        return outcome == ActivityOutcomeKind.Failed
            ? new TransitionDecision.Propagate()
            : new TransitionDecision.End();
    }

    /// <summary>
    /// Back to <paramref name="callActivityName"/> to take the next emitted value. The input is
    /// carried for symmetry with every other transition and is not read: re-entering a live
    /// subscription acknowledges and waits rather than resolving the activity's configuration.
    /// </summary>
    private static TransitionDecision Resume(string callActivityName, object? passThrough)
    {
        return new TransitionDecision.Resume(
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
            if (!string.IsNullOrEmpty(branch.Condition)
                && !templateEngine.EvaluateCondition(branch.Condition, scope))
                continue;

            if (branch.ForEach != null)
            {
                // Dynamic expansion: evaluate collection, iterate with alias. The collection is a
                // whole-field template that must yield an array; a string, an object or nothing
                // is an authoring error, not an empty expansion.
                var rendered = templateEngine.RenderTyped(branch.ForEach.Collection, scope);
                if (rendered is not IEnumerable<object?> items || rendered is string)
                    throw new TemplateException("UTOS-E105",
                        $"forEach collection `{branch.ForEach.Collection}` must evaluate to an array.");

                var collection = items.ToList();

                var scopes = collection
                    .Select(item => scope.With(branch.ForEach.Alias, item))
                    .ToList();

                var names = scopes.Select(itemScope => RenderBranchName(branch, itemScope)).ToList();

                // The ordinal is a fallback, not the rule. A templated name renders to something
                // different per item and stands on its own — `addon-SKU-123` says which expansion
                // failed where `addon_0` says nothing, which is the whole reason the name is
                // templatable. A *literal* name renders to itself every time, so without the
                // suffix an expansion over three items would claim one key three times and lose
                // two results. Deciding per expansion rather than per author keeps both working
                // and is why `forEach` with a plain name needs no migration.
                var distinct = names.Distinct(StringComparer.Ordinal).Count() == names.Count;

                for (var index = 0; index < scopes.Count; index++)
                    branches.Add(PlanBranch(branch,
                        distinct ? names[index] : $"{names[index]}_{index}",
                        scopes[index], activityInput));
            }
            else
            {
                // Static branch: single resolution. Its name is rendered too — a template with
                // nothing to bind renders to itself, so this costs nothing and means one rule.
                branches.Add(PlanBranch(branch, RenderBranchName(branch, scope), scope, activityInput));
            }
        }

        // Branch names key the promise output map, so two branches answering to one name would
        // lose a result silently. Within one for_each expansion the ordinal above has already
        // settled it; what is left is a collision *between* branches, which no ordinal can fix
        // because disambiguating would have to rename a branch the author named deliberately.
        // The validator catches the literal case (UTOS-C307) and cannot catch this one, since
        // rendering needs the collection.
        var duplicate = branches
            .GroupBy(plan => plan.BranchName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate != null)
            throw new InvalidOperationException(
                $"Promise branch name '{duplicate.Key}' is not unique: {duplicate.Count()} branches "
                + "rendered to it. Branch names key the promise output map and must be distinct.");

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

    /// <summary>
    /// The branch's key in the promise output map, rendered in <paramref name="scope"/> so a
    /// for_each expansion can be named after the item it processes. Ordinals are deliberately not
    /// appended: <c>item_0</c> says nothing about which expansion failed, which is the whole
    /// reason the name is templatable.
    /// </summary>
    private string RenderBranchName(PromiseBranch branch, TemplateScope scope) =>
        templateEngine.RenderToString(branch.Name, scope);

    private PromiseBranchPlan PlanBranch(
        PromiseBranch branch,
        string branchName,
        TemplateScope scope,
        IReadOnlyDictionary<string, object>? activityInput)
    {
        // A branch names a document, so there is nothing to look up in this workflow and no
        // activity kind to reason about. The bundle is self-contained and UTOS-C501-C503 already
        // checked that the document and its entry activity exist, so this only renders input.
        var branchInput = branch.Input != null
            ? structRenderer.Render(branch.Input, scope)
            : AsMutable(activityInput);

        return new PromiseBranchPlan(branchName, branch.Workflow, branch.StartActivity, branchInput);
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
