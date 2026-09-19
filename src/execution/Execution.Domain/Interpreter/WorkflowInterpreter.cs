using Common.Workflow;
using Execution.Domain.Schemas;
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
public sealed class WorkflowInterpreter(
    ITemplateEngine templateEngine,
    StructRenderer structRenderer,
    ISchemaValidator schemaValidator)
{
    /// <summary>
    /// Applies a declaration to a value the interpreter has just rendered, and throws
    /// <see cref="SchemaValidationFailedException"/> if it does not satisfy it.
    /// <para>
    /// Deliberately the same shape at every boundary: the check is the same one, and what differs
    /// is only the code it carries and — outside this class — whether an <c>onFailure</c> rule
    /// can catch it.
    /// </para>
    /// </summary>
    private Dictionary<string, object>? Check(
        ValueMap? schema, Dictionary<string, object>? value, SchemaBoundary boundary, string code)
    {
        if (schema is null || schema.Count == 0) return value;

        var subject = ValueMap.From(
            (value ?? []).Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)));

        var result = schemaValidator.Validate(schema, subject, boundary);

        if (!result.IsValid) throw new SchemaValidationFailedException(code, boundary, result);

        // Defaults are filled at an inbound boundary, and the filled value is what travels on —
        // so a replay sees exactly what the activity saw.
        return result.Value.Fields
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!);
    }

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
    /// <param name="moment">
    /// The instant and seed this evaluation's expressions read time and randomness from — captured
    /// once by the caller, so every expression of the decision agrees and a replay agrees with the
    /// first run. Null is the fixed <see cref="EvaluationMoment.Unset"/>, for callers with no clock
    /// of their own.
    /// </param>
    public TransitionDecision Advance(
        WorkflowSession session,
        string activityName,
        IReadOnlyDictionary<string, object>? activityInput,
        object? activityOutput,
        ActivityOutcomeKind outcome = ActivityOutcomeKind.Succeeded,
        HttpResponseContext? response = null,
        WorkflowErrorContext? error = null,
        EvaluationMoment? moment = null)
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
            .WithMoment(moment)
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
                session,
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
                { Transition: not null } => Goto(session, rule.Transition, scope, passThrough),

                { Result: not null } => Return(session, rule.Result, scope),

                { Emit: not null } => Emit(session, rule.Emit, scope, passThrough, activityName),

                { Error: not null } => Fail(rule.Error, scope,
                    failureInScope: outcome == ActivityOutcomeKind.Failed),

                _ => throw new InvalidOperationException(
                    "Transition rule has no action set (none of transition, result, emit or error)")
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
        WorkflowSession session,
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
                // UTOS-H104 again: a handler dispatch starts a document, so its input answers to
                // that document's start activity like any other invocation.
                { Handle: not null } => new TransitionDecision.Dispatch(
                    rule.Handle.Workflow,
                    rule.Handle.StartActivity,
                    Check(
                        session.DeclaredInputOf(rule.Handle.Workflow, rule.Handle.StartActivity),
                        rule.Handle.Input != null
                            ? structRenderer.Render(rule.Handle.Input, scope)
                            : passThrough as Dictionary<string, object>,
                        SchemaBoundary.Input,
                        SchemaValidation.InvocationInput)),

                { Transition: not null } => Goto(session, rule.Transition, scope, passThrough),

                { Result: not null } => Return(session, rule.Result, scope),

                // A value arrived, not a failure: an empty error has nothing to re-raise here, and
                // UTOS-T005 refuses one at load.
                { Error: not null } => Fail(rule.Error, scope, failureInScope: false),

                _ => throw new InvalidOperationException(
                    "Emission rule has no action set (none of handle, transition, result or error)")
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
        IReadOnlyDictionary<string, object>? activityInput,
        EvaluationMoment? moment = null)
    {
        var activity = Require(session, activityName);
        var input = session.InputFor(activityInput);

        var scope = new TemplateScope()
            .WithMoment(moment)
            .WithInput(input)
            .WithEnv(session.Env);

        return activity.Config switch
        {
            HttpActivityConfig http => ResolveHttp(http, scope, activityName),

            SubWorkflowActivityConfig subWorkflow =>
                ResolveSubWorkflow(session, subWorkflow, scope, activityInput),

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
        IReadOnlyDictionary<string, object>? activityInput,
        EvaluationMoment? moment = null)
    {
        var activity = Require(session, activityName);

        if (activity.Config is not PromiseActivityConfig promise)
            throw new InvalidOperationException(
                $"Activity '{activityName}' is not a promise activity.");

        var input = session.InputFor(activityInput);

        var scope = new TemplateScope()
            .WithMoment(moment)
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
                var collection = ForEachCollection.Evaluate(templateEngine, branch.ForEach.Collection, scope);

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
                    branches.Add(PlanBranch(session, branch,
                        distinct ? names[index] : $"{names[index]}_{index}",
                        scopes[index], activityInput));
            }
            else
            {
                // Static branch: single resolution. Its name is rendered too — a template with
                // nothing to bind renders to itself, so this costs nothing and means one rule.
                branches.Add(PlanBranch(session, branch, RenderBranchName(branch, scope), scope, activityInput));
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

    private TransitionDecision Goto(
        WorkflowSession session, TransitionTarget target, TemplateScope scope, object? activityOutput)
    {
        return new TransitionDecision.Goto(
            target.Name, ResolveTransitionInput(session, target, scope, activityOutput));
    }

    /// <summary>
    /// An empty map is "end with no value", per <c>activity.proto</c>: nothing is rendered, and the
    /// decision carries null so the run ends exactly as it does when its rules run out. Anything
    /// else is rendered, and a map whose templates all render to nothing is still a value — the
    /// author wrote fields.
    /// </summary>
    private TransitionDecision Return(WorkflowSession session, ValueMap result, TemplateScope scope)
    {
        // UTOS-H105. Checked even when the path ends with no value: a workflow that declares an
        // output and returns nothing has not satisfied its contract, and the empty map is what
        // `required` is there to catch.
        var value = Check(
            session.Graph.Workflow.Spec.Output,
            result.Fields.Count == 0 ? null : structRenderer.Render(result, scope),
            SchemaBoundary.Output,
            SchemaValidation.Result);

        return new TransitionDecision.Return(result.Fields.Count == 0 ? null : value);
    }

    /// <summary>
    /// The reason is rendered here, in the rule's own scope, so that <c>{{ error.message }}</c>
    /// and <c>{{ response.status }}</c> mean what they mean on every other field of the rule. The
    /// code is never rendered: it is the literal a consumer switches on.
    /// <para>
    /// An error with no fields, where a failure is in scope, is the re-raise (spec 0.0.17): the
    /// failure being handled continues exactly as if no rule had matched it — the same exception,
    /// so the same code, message and details, and a sub-workflow's authored failure stays that
    /// failure. That is <see cref="TransitionDecision.Propagate"/>, carrying the rule that matched
    /// so the log still says which one re-raised it.
    /// </para>
    /// </summary>
    private TransitionDecision Fail(ErrorAction error, TemplateScope scope, bool failureInScope)
    {
        if (failureInScope && IsReraise(error))
            return new TransitionDecision.Propagate();

        // UTOS-T005 rejects an empty code at load; this is the backstop for a bundle that reached
        // the interpreter without it, so the run still reports *something* a client can match.
        var code = string.IsNullOrEmpty(error.Code) ? WorkflowErrorContext.UnspecifiedCode : error.Code;

        var message = error.Message == null
            ? string.Empty
            : templateEngine.RenderToString(error.Message, scope);

        return new TransitionDecision.Fail(
            code,
            message,
            error.Details == null ? null : structRenderer.Render(error.Details, scope));
    }

    /// <summary>
    /// No code, no message template, no details — the wire's empty <c>WorkflowError</c>, which the
    /// source format writes as a bare <c>error</c>. A partly written one is not a re-raise; it is
    /// refused at load (<c>UTOS-T005</c>) and reaches <see cref="Fail"/>'s backstop otherwise.
    /// </summary>
    private static bool IsReraise(ErrorAction error) =>
        string.IsNullOrEmpty(error.Code) && error.Message == null && error.Details == null;

    /// <summary>
    /// The value to append and where the run goes once it is durable. The target is required —
    /// an emit is not a terminal action, so a rule that emits and goes nowhere is a dead end
    /// rather than a return. <c>UTOS-T004</c> rejects that at load; this is the backstop for a
    /// bundle that reached the interpreter without it.
    /// </summary>
    private TransitionDecision Emit(
        WorkflowSession session, EmitAction emit, TemplateScope scope, object? activityOutput,
        string activityName)
    {
        var target = emit.Transition
                     ?? throw new InvalidOperationException(
                         $"Emit rule on activity '{activityName}' has no transition target.");

        // UTOS-H106. `spec.emits` describes one emitted value, and an emit that violates it fails
        // the run for the same reason a bad result does: a consumer's onEmitted rules depend on
        // that declaration exactly as a caller depends on a result.
        var value = Check(
            session.Graph.Workflow.Spec.Emits,
            structRenderer.Render(emit.Value ?? ValueMap.Empty, scope),
            SchemaBoundary.Emits,
            SchemaValidation.EmittedValue);

        return new TransitionDecision.Emit(
            value,
            target.Name,
            ResolveTransitionInput(session, target, scope, activityOutput));
    }

    /// <summary>
    /// Resolves the mapped input for the target activity.
    /// If the transition target has an explicit input transform, renders it.
    /// Otherwise, passes the raw activity output through as-is.
    /// <para>
    /// The result is then checked against what the target declares (<c>UTOS-H103</c>), including
    /// the pass-through case: an activity's declaration is about the data it receives, not about
    /// how the previous one chose to hand it over.
    /// </para>
    /// </summary>
    private Dictionary<string, object>? ResolveTransitionInput(
        WorkflowSession session,
        TransitionTarget target,
        TemplateScope scope,
        object? activityOutput)
    {
        var resolved = target.Input != null
            ? structRenderer.Render(target.Input, scope)
            // Pass-through: raw output becomes the next activity's input
            : activityOutput as Dictionary<string, object>;

        var declared = session.Graph.TryGetActivity(target.Name, out var destination)
            ? destination.InputSchema
            : null;

        return Check(declared, resolved, SchemaBoundary.Input, SchemaValidation.TransitionInput);
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
        WorkflowSession session,
        SubWorkflowActivityConfig config,
        TemplateScope scope,
        IReadOnlyDictionary<string, object>? rawActivityInput)
    {
        var rendered = config.Input != null
            ? structRenderer.Render(config.Input, scope)
            : AsMutable(rawActivityInput);

        // UTOS-H104, against the callee's start activity. Unlike the other three this failure is
        // catchable: it is the calling activity failing, so its own onFailure rules apply as they
        // would to any other failure of that activity.
        var subInput = Check(
            session.DeclaredInputOf(config.Workflow, config.StartActivity),
            rendered,
            SchemaBoundary.Input,
            SchemaValidation.InvocationInput);

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
        WorkflowSession session,
        PromiseBranch branch,
        string branchName,
        TemplateScope scope,
        IReadOnlyDictionary<string, object>? activityInput)
    {
        // A branch names a document, so there is nothing to look up in this workflow and no
        // activity kind to reason about. The bundle is self-contained and UTOS-C501-C503 already
        // checked that the document and its entry activity exist, so this renders input and
        // checks it against what that entry activity declares (UTOS-H104).
        var rendered = branch.Input != null
            ? structRenderer.Render(branch.Input, scope)
            : AsMutable(activityInput);

        var branchInput = Check(
            session.DeclaredInputOf(branch.Workflow, branch.StartActivity),
            rendered,
            SchemaBoundary.Input,
            SchemaValidation.InvocationInput);

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
