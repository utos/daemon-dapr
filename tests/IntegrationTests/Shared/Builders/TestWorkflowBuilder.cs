using Common.Constants;
using Google.Protobuf.WellKnownTypes;
using Utos.Daemon.V1;
using Utos.Workflows.V1;

namespace IntegrationTests.Shared.Builders;

/// <summary>
/// What a test wants to run. Since v0.0.10 a bundle is no longer shipped on the schedule
/// call, so this is loaded into the daemon's definition store first and then scheduled by
/// reference — see <c>DaemonTestFixture.ScheduleAsync</c>.
/// </summary>
public sealed record TestWorkflowRequest(
    WorkflowBundle Bundle,
    ExecutionPayload Input,
    string StartActivity,
    Dictionary<string, string> Env);

public class TestWorkflowBuilder
{
    private readonly string _workflowName;
    private readonly string _mockBaseUrl;
    private readonly string _version;
    private readonly string? _description;
    private readonly List<(string Name, WorkflowActivity Activity)> _activities = [];
    private readonly ExecutionPayload _input = new();
    private readonly Dictionary<string, string> _env = new();
    private readonly Dictionary<string, Workflow> _dependentWorkflows = new();

    public TestWorkflowBuilder(string workflowName, string mockBaseUrl,
        string? version = null, string? description = null)
    {
        _workflowName = workflowName;
        _mockBaseUrl = mockBaseUrl;
        _version = version ?? "1.0.0";
        _description = description ?? "Test workflow";
    }

    public ActivityBuilder HttpGet(string name, string path, string? customUrl = null)
    {
        return AddHttpActivity(name, path, "GET", null, customUrl);
    }

    public ActivityBuilder HttpPost(string name, string path, string? body = null, string? customUrl = null)
    {
        return AddHttpActivity(name, path, "POST", body, customUrl);
    }

    public ActivityBuilder HttpPut(string name, string path, string? body = null, string? customUrl = null)
    {
        return AddHttpActivity(name, path, "PUT", body, customUrl);
    }

    public ActivityBuilder HttpDelete(string name, string path, string? customUrl = null)
    {
        return AddHttpActivity(name, path, "DELETE", null, customUrl);
    }

    /// <summary>
    /// Awaits the sub-workflow; its result becomes the activity's output.
    /// <paramref name="onEmitted"/> makes this caller the child's privileged consumer — without
    /// it the caller sees only the terminal value, and the child emits without waiting.
    /// </summary>
    public ActivityBuilder WorkflowCall(string activityName, string targetWorkflow,
        string startActivity, Struct? input = null,
        IEnumerable<DispatchRule>? onEmitted = null) =>
        SubWorkflow(activityName, targetWorkflow, startActivity, input,
            config =>
            {
                var call = new CallActivityConfig();
                if (onEmitted != null)
                    call.OnEmitted.AddRange(onEmitted);
                config.Call = call;
            });

    /// <summary>
    /// Starts the sub-workflow and moves on. The activity's output is a handle to the run rather
    /// than a result, and the child outlives the parent.
    /// </summary>
    public ActivityBuilder WorkflowSpawn(string activityName, string targetWorkflow,
        string startActivity, Struct? input = null) =>
        SubWorkflow(activityName, targetWorkflow, startActivity, input,
            config => config.Spawn = new SpawnActivityConfig());

    private ActivityBuilder SubWorkflow(string activityName, string targetWorkflow,
        string startActivity, Struct? input, Action<WorkflowActivityConfig> setMode)
    {
        var config = new WorkflowActivityConfig
        {
            Workflow = targetWorkflow,
            StartActivity = startActivity
        };

        setMode(config);

        if (input != null)
            config.Input = input;

        var activity = new WorkflowActivity { Workflow = config };
        _activities.Add((activityName, activity));
        return new ActivityBuilder(this, activity);
    }

    public TestWorkflowBuilder AddDependentWorkflow(string workflowName, Action<TestWorkflowBuilder> configure)
    {
        var childBuilder = new TestWorkflowBuilder(workflowName, _mockBaseUrl);
        configure(childBuilder);

        var spec = new WorkflowSpec();
        foreach (var (name, activity) in childBuilder._activities)
            spec.Activities.Add(name, activity);

        var workflow = new Workflow
        {
            ApiVersion = WorkflowDocument.ApiVersion,
            Kind = WorkflowDocument.Kind,
            Metadata = new WorkflowMetadata
            {
                Name = workflowName,
                Version = childBuilder._version,
                Description = childBuilder._description ?? "Dependent workflow"
            },
            Spec = spec
        };

        _dependentWorkflows[workflowName] = workflow;
        return this;
    }

    public TestWorkflowBuilder WithInput(string key, object? value)
    {
        _input.Data.Add(key, ToProtobufValue(value));
        return this;
    }

    public TestWorkflowBuilder WithInput(ExecutionPayload payload)
    {
        foreach (var kvp in payload.Data)
            _input.Data[kvp.Key] = kvp.Value;
        return this;
    }

    /// <summary>Ambient config for the run, reachable from every activity as {{ env.key }}.</summary>
    public TestWorkflowBuilder WithEnv(string key, string value)
    {
        _env[key] = value;
        return this;
    }

    public TestWorkflowRequest Build(string? startActivity = null)
    {
        var spec = new WorkflowSpec();
        foreach (var (name, activity) in _activities)
            spec.Activities.Add(name, activity);

        var workflow = new Workflow
        {
            ApiVersion = WorkflowDocument.ApiVersion,
            Kind = WorkflowDocument.Kind,
            Metadata = new WorkflowMetadata
            {
                Name = _workflowName,
                Version = _version,
                Description = _description ?? "Test workflow"
            },
            Spec = spec
        };

        // A sub-workflow activity names its target by canonical identity once the bundle is built
        // (UTOS-B006), while a test names it by workflow name — resolved here, the same way the
        // CLI resolves a dependency alias. Every workflow in the bundle is rewritten, not just the
        // entry point, because a dependent may itself call another one.
        var identities = _dependentWorkflows.ToDictionary(
            dependent => dependent.Key,
            dependent => WorkflowIdentity.FormatMetadata(dependent.Value.Metadata));

        // Keyed by canonical identity rather than by name: UTOS-B005 requires every key to equal
        // the identity derived from that workflow's own metadata, which is what makes a bundle
        // self-describing.
        var identity = WorkflowIdentity.FormatMetadata(workflow.Metadata);

        ResolveSubWorkflowReferences(spec, identities, identity);
        foreach (var (name, dependent) in _dependentWorkflows)
            ResolveSubWorkflowReferences(dependent.Spec, identities, identities[name]);

        var bundle = new WorkflowBundle { EntryPoint = identity };
        bundle.Workflows.Add(identity, workflow);

        foreach (var (name, depWorkflow) in _dependentWorkflows)
            bundle.Workflows.Add(identities[name], depWorkflow);

        return new TestWorkflowRequest(
            bundle,
            _input,
            startActivity ?? (_activities.Count > 0 ? _activities[0].Name : ""),
            _env);
    }

    /// <summary>
    /// Rewrites every place a document is named — a sub-workflow activity, a promise branch, an
    /// <c>onEmitted</c> rule — from a workflow name to the canonical identity it resolves to,
    /// standing in for what the CLI does to a dependency alias.
    /// <para>
    /// <c>self</c> resolves to <paramref name="ownIdentity"/>, the identity of the document the
    /// reference is written in, which is why this is called once per document rather than over
    /// the bundle. That is also the only reason a daemon test may write <c>self</c> at all: the
    /// word never reaches a bundle, so resolving it here is what keeps these fixtures readable
    /// without pretending the daemon understands it.
    /// </para>
    /// <para>
    /// Anything unrecognised is left alone, so a test can still assert on a deliberately dangling
    /// reference.
    /// </para>
    /// </summary>
    private static void ResolveSubWorkflowReferences(
        WorkflowSpec spec, IReadOnlyDictionary<string, string> identities, string ownIdentity)
    {
        string Resolve(string reference) =>
            reference == SelfReference ? ownIdentity
            : identities.TryGetValue(reference, out var identity) ? identity
            : reference;

        foreach (var activity in spec.Activities.Values)
        {
            switch (activity.ConfigCase)
            {
                case WorkflowActivity.ConfigOneofCase.Workflow:
                    activity.Workflow.Workflow = Resolve(activity.Workflow.Workflow);

                    if (activity.Workflow.ModeCase == WorkflowActivityConfig.ModeOneofCase.Call)
                        foreach (var rule in activity.Workflow.Call.OnEmitted)
                            rule.Workflow = Resolve(rule.Workflow);

                    break;

                case WorkflowActivity.ConfigOneofCase.Promise:
                    foreach (var branch in activity.Promise.Branches)
                        branch.Workflow = Resolve(branch.Workflow);

                    break;
            }
        }
    }

    /// <summary>
    /// What a test writes to dispatch the document under construction. Resolved away in
    /// <see cref="Build"/>, exactly as the CLI resolves it before a bundle exists.
    /// </summary>
    public const string SelfReference = "self";

    public TestWorkflowRequest BuildSequential(string? startActivity = null)
    {
        for (var i = 0; i < _activities.Count - 1; i++)
            _activities[i].Activity.OnSuccess.Add(new TransitionRule
            {
                Transition = new TransitionTarget { Name = _activities[i + 1].Name }
            });

        return Build(startActivity);
    }

    internal ActivityBuilder AddHttpActivity(string name, string path, string method,
        string? body, string? customUrl)
    {
        var config = new HttpActivityConfig
        {
            Method = method,
            Url = customUrl ?? $"{_mockBaseUrl}{path}"
        };

        if (body != null)
            config.Body = body;

        var activity = new WorkflowActivity { Http = config };
        _activities.Add((name, activity));
        return new ActivityBuilder(this, activity);
    }

    /// <summary>A durable delay. Its input passes through unchanged as its output.</summary>
    public ActivityBuilder Timer(string name, TimeSpan duration)
    {
        var activity = new WorkflowActivity
        {
            Timer = new TimerActivityConfig { Duration = Duration.FromTimeSpan(duration) }
        };

        _activities.Add((name, activity));
        return new ActivityBuilder(this, activity);
    }

    /// <summary>Builds a timer whose duration is set directly, for validation tests.</summary>
    public ActivityBuilder RawTimer(string name, Duration? duration)
    {
        var config = new TimerActivityConfig();
        if (duration != null)
            config.Duration = duration;

        var activity = new WorkflowActivity { Timer = config };
        _activities.Add((name, activity));
        return new ActivityBuilder(this, activity);
    }

    /// <summary>
    /// Fan-out. <paramref name="mode"/> stays a string so the suites keep reading the way a
    /// workflow is authored; it selects the completion oneof rather than being carried as one.
    /// </summary>
    public PromiseBranchListBuilder Promise(string activityName, string mode, int requiredCount = 0)
    {
        var config = new PromiseActivityConfig();

        switch (mode)
        {
            case "all": config.All = new PromiseAllConfig(); break;
            case "any": config.Any = new PromiseAnyConfig(); break;
            case "race": config.Race = new PromiseRaceConfig(); break;
            case "count": config.Count = new PromiseCountConfig { RequiredCount = requiredCount }; break;
            default: throw new ArgumentException($"Unknown promise mode '{mode}'.", nameof(mode));
        }

        var activity = new WorkflowActivity { Promise = config };
        _activities.Add((activityName, activity));
        return new PromiseBranchListBuilder(this, activity);
    }

    internal TestWorkflowBuilder AddActivity(string name, WorkflowActivity activity)
    {
        _activities.Add((name, activity));
        return this;
    }

    private static Value ToProtobufValue(object? value)
    {
        return value switch
        {
            null => Value.ForNull(),
            string s => Value.ForString(s),
            int i => Value.ForNumber(i),
            long l => Value.ForNumber(l),
            double d => Value.ForNumber(d),
            float f => Value.ForNumber(f),
            bool b => Value.ForBool(b),
            Value v => v,
            _ => throw new ArgumentException($"Unsupported type: {value.GetType().Name}")
        };
    }
}

public class ActivityBuilder
{
    private readonly TestWorkflowBuilder _parent;
    private readonly WorkflowActivity _activity;

    internal ActivityBuilder(TestWorkflowBuilder parent, WorkflowActivity activity)
    {
        _parent = parent;
        _activity = activity;
    }

    public ActivityBuilder OnSuccess(string targetActivity, Struct? input = null, string? when = null)
    {
        var target = new TransitionTarget { Name = targetActivity };
        if (input != null)
            target.Input = input;

        var rule = new TransitionRule { Transition = target };

        if (when != null)
            rule.Condition = when;

        _activity.OnSuccess.Add(rule);
        return this;
    }

    public ActivityBuilder OnResult(Struct resultValue, string? when = null)
    {
        var rule = new TransitionRule { Result = resultValue };

        if (when != null)
            rule.Condition = when;

        _activity.OnSuccess.Add(rule);
        return this;
    }

    /// <summary>
    /// Where the run goes when this activity fails. Note the asymmetry with
    /// <see cref="OnSuccess"/>: if no rule here matches, the failure is unhandled and keeps
    /// travelling, rather than the run completing.
    /// </summary>
    public ActivityBuilder OnFailure(string targetActivity, Struct? input = null, string? when = null)
    {
        var target = new TransitionTarget { Name = targetActivity };
        if (input != null)
            target.Input = input;

        var rule = new TransitionRule { Transition = target };

        if (when != null)
            rule.Condition = when;

        _activity.OnFailure.Add(rule);
        return this;
    }

    /// <summary>Ends the run with <paramref name="resultValue"/> when this activity fails.</summary>
    public ActivityBuilder OnFailureResult(Struct resultValue, string? when = null)
    {
        var rule = new TransitionRule { Result = resultValue };

        if (when != null)
            rule.Condition = when;

        _activity.OnFailure.Add(rule);
        return this;
    }

    /// <summary>
    /// Appends <paramref name="value"/> to the run's output stream and carries on to
    /// <paramref name="targetActivity"/>. Emit-and-continue, as against <see cref="OnResult"/>'s
    /// emit-and-terminate.
    /// </summary>
    public ActivityBuilder OnEmit(Struct value, string targetActivity,
        Struct? input = null, string? when = null)
    {
        var target = new TransitionTarget { Name = targetActivity };
        if (input != null)
            target.Input = input;

        var rule = new TransitionRule { Emit = new EmitAction { Value = value, Transition = target } };

        if (when != null)
            rule.Condition = when;

        _activity.OnSuccess.Add(rule);
        return this;
    }

    public ActivityBuilder WithHeader(string name, string value)
    {
        _activity.Http.Headers.Add(name, value);
        return this;
    }

    public ActivityBuilder HttpGet(string name, string path, string? customUrl = null)
    {
        return _parent.HttpGet(name, path, customUrl);
    }

    public ActivityBuilder HttpPost(string name, string path, string? body = null, string? customUrl = null)
    {
        return _parent.HttpPost(name, path, body, customUrl);
    }

    public ActivityBuilder HttpPut(string name, string path, string? body = null, string? customUrl = null)
    {
        return _parent.HttpPut(name, path, body, customUrl);
    }

    public ActivityBuilder HttpDelete(string name, string path, string? customUrl = null)
    {
        return _parent.HttpDelete(name, path, customUrl);
    }

    public ActivityBuilder WorkflowCall(string activityName, string targetWorkflow,
        string startActivity, Struct? input = null)
    {
        return _parent.WorkflowCall(activityName, targetWorkflow, startActivity, input);
    }

    public ActivityBuilder WorkflowSpawn(string activityName, string targetWorkflow,
        string startActivity, Struct? input = null)
    {
        return _parent.WorkflowSpawn(activityName, targetWorkflow, startActivity, input);
    }

    public PromiseBranchListBuilder Promise(string activityName, string mode, int requiredCount = 0)
    {
        return _parent.Promise(activityName, mode, requiredCount);
    }

    public ActivityBuilder Timer(string name, TimeSpan duration)
    {
        return _parent.Timer(name, duration);
    }

    public TestWorkflowBuilder AddDependentWorkflow(string workflowName, Action<TestWorkflowBuilder> configure)
    {
        return _parent.AddDependentWorkflow(workflowName, configure);
    }

    public TestWorkflowBuilder WithInput(string key, object? value)
    {
        return _parent.WithInput(key, value);
    }

    public TestWorkflowBuilder WithInput(ExecutionPayload payload)
    {
        return _parent.WithInput(payload);
    }

    public TestWorkflowBuilder WithEnv(string key, string value)
    {
        return _parent.WithEnv(key, value);
    }

    public TestWorkflowRequest Build(string? startActivity = null)
    {
        return _parent.Build(startActivity);
    }

    public TestWorkflowRequest BuildSequential(string? startActivity = null)
    {
        return _parent.BuildSequential(startActivity);
    }
}

public class PromiseBranchListBuilder
{
    private readonly TestWorkflowBuilder _parent;
    private readonly WorkflowActivity _activity;

    internal PromiseBranchListBuilder(TestWorkflowBuilder parent, WorkflowActivity activity)
    {
        _parent = parent;
        _activity = activity;
    }

    /// <summary>
    /// A branch dispatching <paramref name="startActivity"/> of <paramref name="workflow"/>.
    /// <para>
    /// <paramref name="workflow"/> is a canonical identity, not an alias: these fixtures are
    /// bundles, and the CLI has already resolved every alias — including <c>self</c> — by the time
    /// one exists. Fanning out over the document under construction means passing its own key,
    /// which <see cref="TestWorkflowBuilder.SelfKey"/> supplies.
    /// </para>
    /// </summary>
    public PromiseBranchListBuilder Branch(string name, string workflow, string startActivity,
        Struct? input = null, string? condition = null)
    {
        var branch = new PromiseBranch
        {
            Name = name,
            Workflow = workflow,
            StartActivity = startActivity
        };

        if (input != null)
            branch.Input = input;

        if (condition != null)
            branch.Condition = condition;

        _activity.Promise.Branches.Add(branch);
        return this;
    }

    public PromiseBranchListBuilder ForEachBranch(string name, string workflow,
        string startActivity, string collection, string alias, Struct? input = null)
    {
        var branch = new PromiseBranch
        {
            Name = name,
            Workflow = workflow,
            StartActivity = startActivity,
            ForEach = new ForEachConfig
            {
                Collection = collection,
                Alias = alias
            }
        };

        if (input != null)
            branch.Input = input;

        _activity.Promise.Branches.Add(branch);
        return this;
    }

    public PromiseBranchListBuilder OnSuccess(string targetActivity, Struct? input = null, string? when = null)
    {
        var target = new TransitionTarget { Name = targetActivity };
        if (input != null)
            target.Input = input;

        var rule = new TransitionRule { Transition = target };
        if (when != null)
            rule.Condition = when;

        _activity.OnSuccess.Add(rule);
        return this;
    }

    public PromiseBranchListBuilder OnResult(Struct resultValue, string? when = null)
    {
        var rule = new TransitionRule { Result = resultValue };
        if (when != null)
            rule.Condition = when;

        _activity.OnSuccess.Add(rule);
        return this;
    }

    /// <summary>Where the run goes when the promise itself fails — a mode that cannot be satisfied.</summary>
    public PromiseBranchListBuilder OnFailure(string targetActivity, Struct? input = null, string? when = null)
    {
        var target = new TransitionTarget { Name = targetActivity };
        if (input != null)
            target.Input = input;

        var rule = new TransitionRule { Transition = target };
        if (when != null)
            rule.Condition = when;

        _activity.OnFailure.Add(rule);
        return this;
    }

    public TestWorkflowBuilder AddDependentWorkflow(string workflowName, Action<TestWorkflowBuilder> configure)
    {
        return _parent.AddDependentWorkflow(workflowName, configure);
    }

    public TestWorkflowBuilder WithInput(string key, object? value)
    {
        return _parent.WithInput(key, value);
    }

    public TestWorkflowBuilder WithEnv(string key, string value)
    {
        return _parent.WithEnv(key, value);
    }

    public TestWorkflowRequest Build(string? startActivity = null)
    {
        return _parent.Build(startActivity);
    }

    public TestWorkflowRequest BuildSequential(string? startActivity = null)
    {
        return _parent.BuildSequential(startActivity);
    }
}
