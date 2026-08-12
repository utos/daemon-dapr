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
    public TransitionDecision Advance(
        WorkflowSession session,
        string activityName,
        IReadOnlyDictionary<string, object>? activityInput,
        object? activityOutput)
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

        // If no transitions, workflow ends
        if (activity.OnSuccess.Count == 0)
            return new TransitionDecision.End();

        var scope = new TemplateScope()
            .WithInput(input)
            .WithOutput(output)
            .WithEnv(session.Env);

        foreach (var rule in activity.OnSuccess)
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
                { Transition: not null } => Goto(rule.Transition, scope, output),

                { Result: not null } =>
                    new TransitionDecision.Return(structRenderer.Render(rule.Result, scope)),

                _ => throw new InvalidOperationException(
                    "Transition rule has no action set (neither transition nor result)")
            };
        }

        // No transitions matched - workflow ends
        return new TransitionDecision.End();
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
        // The target.Name references an activity in the current workflow.
        // That activity must be a workflow-type activity.
        var targetActivity = session.Graph.Find(target.Name)
                             ?? throw new InvalidOperationException(
                                 $"Branch target activity '{target.Name}' not found in workflow");

        if (targetActivity.Config is not SubWorkflowActivityConfig config)
            throw new InvalidOperationException(
                $"Branch target activity '{target.Name}' must be a workflow-type activity");

        // Render branch input from target.Input if set, otherwise pass through activity input
        var branchInput = target.Input != null
            ? structRenderer.Render(target.Input, scope)
            : AsMutable(activityInput);

        return new PromiseBranchPlan(branchName, config.Workflow, config.StartActivity, branchInput);
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
