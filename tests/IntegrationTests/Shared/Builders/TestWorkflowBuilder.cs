using Google.Protobuf.WellKnownTypes;
using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace IntegrationTests.Shared.Builders;

public class TestWorkflowBuilder
{
    private readonly string _workflowName;
    private readonly string _mockBaseUrl;
    private readonly string _version;
    private readonly string? _description;
    private readonly List<(string Name, WorkflowActivity Activity)> _activities = [];
    private readonly ExecutionPayload _input = new();
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

    public ActivityBuilder WorkflowCall(string activityName, string targetWorkflow,
        string startActivity, Struct? input = null)
    {
        var config = new WorkflowActivityConfig
        {
            Workflow = targetWorkflow,
            StartActivity = startActivity
        };

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
            ApiVersion = WorkflowApiVersion.V100,
            Kind = WorkflowKind.WorkflowUtos,
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

    public ScheduleExecutionRequest Build(string? startActivity = null)
    {
        var spec = new WorkflowSpec();
        foreach (var (name, activity) in _activities)
            spec.Activities.Add(name, activity);

        var workflow = new Workflow
        {
            ApiVersion = WorkflowApiVersion.V100,
            Kind = WorkflowKind.WorkflowUtos,
            Metadata = new WorkflowMetadata
            {
                Name = _workflowName,
                Version = _version,
                Description = _description ?? "Test workflow"
            },
            Spec = spec
        };

        var bundle = new WorkflowBundle { EntryPoint = _workflowName };
        bundle.Workflows.Add(_workflowName, workflow);

        foreach (var (name, depWorkflow) in _dependentWorkflows)
            bundle.Workflows.Add(name, depWorkflow);

        return new ScheduleExecutionRequest
        {
            Bundle = bundle,
            Input = _input,
            StartActivity = startActivity ?? (_activities.Count > 0 ? _activities[0].Name : "")
        };
    }

    public ScheduleExecutionRequest BuildSequential(string? startActivity = null)
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

    public PromiseBranchListBuilder Promise(string activityName, string mode, int requiredCount = 0)
    {
        var config = new PromiseActivityConfig { Mode = mode };
        if (requiredCount > 0)
            config.RequiredCount = requiredCount;

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

    public ActivityBuilder OnReturn(Struct returnValue, string? when = null)
    {
        var rule = new TransitionRule { Return = returnValue };

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

    public PromiseBranchListBuilder Promise(string activityName, string mode, int requiredCount = 0)
    {
        return _parent.Promise(activityName, mode, requiredCount);
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

    public ScheduleExecutionRequest Build(string? startActivity = null)
    {
        return _parent.Build(startActivity);
    }

    public ScheduleExecutionRequest BuildSequential(string? startActivity = null)
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

    public PromiseBranchListBuilder Branch(string name, string targetActivity,
        Struct? input = null, string? condition = null)
    {
        var branch = new PromiseBranch
        {
            Name = name,
            Target = new TransitionTarget { Name = targetActivity }
        };

        if (input != null)
            branch.Target.Input = input;

        if (condition != null)
            branch.Condition = condition;

        _activity.Promise.Branches.Add(branch);
        return this;
    }

    public PromiseBranchListBuilder ForEachBranch(string name, string targetActivity,
        string collection, string alias, Struct? input = null)
    {
        var branch = new PromiseBranch
        {
            Name = name,
            Target = new TransitionTarget { Name = targetActivity },
            ForEach = new ForEachConfig
            {
                Collection = collection,
                Alias = alias
            }
        };

        if (input != null)
            branch.Target.Input = input;

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

    public PromiseBranchListBuilder OnReturn(Struct returnValue, string? when = null)
    {
        var rule = new TransitionRule { Return = returnValue };
        if (when != null)
            rule.Condition = when;

        _activity.OnSuccess.Add(rule);
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

    public ScheduleExecutionRequest Build(string? startActivity = null)
    {
        return _parent.Build(startActivity);
    }

    public ScheduleExecutionRequest BuildSequential(string? startActivity = null)
    {
        return _parent.BuildSequential(startActivity);
    }
}
