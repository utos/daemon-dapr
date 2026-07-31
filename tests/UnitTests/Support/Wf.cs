using Execution.Domain.Interpreter;
using Execution.Infrastructure.Templating;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Utos.Workflow.V1;
using Struct = Google.Protobuf.WellKnownTypes.Struct;

namespace UnitTests.Support;

/// <summary>
/// Terse builders for the protobuf shapes the interpreter reads. Kept minimal on purpose —
/// a test should read as the workflow it describes, not as protobuf assembly.
/// </summary>
internal static class Wf
{
    public static WorkflowGraph Graph(params (string Name, WorkflowActivity Activity)[] activities)
    {
        var workflow = new Workflow
        {
            ApiVersion = "utos.dev/v1",
            Kind = "Workflow",
            Metadata = new WorkflowMetadata { Name = "test", Version = "1.0.0" },
            Spec = new WorkflowSpec()
        };

        foreach (var (name, activity) in activities)
            workflow.Spec.Activities[name] = activity;

        return new WorkflowGraph(workflow, "test:1.0.0");
    }

    public static WorkflowSession Session(
        WorkflowGraph graph,
        IReadOnlyDictionary<string, object>? runInput = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        return new WorkflowSession(
            graph,
            runInput ?? new Dictionary<string, object>(),
            env ?? new Dictionary<string, string>());
    }

    public static WorkflowInterpreter Interpreter()
    {
        var engine = new ScribanTemplateEngine(NullLogger<ScribanTemplateEngine>.Instance);
        return new WorkflowInterpreter(engine, new StructRenderer(engine));
    }

    public static ScribanTemplateEngine Engine()
    {
        return new ScribanTemplateEngine(NullLogger<ScribanTemplateEngine>.Instance);
    }

    public static ExecutionLimits Limits(int maxLooped = 10_000, int continueAsNewAfter = 100)
    {
        return new ExecutionLimits(maxLooped, continueAsNewAfter);
    }

    // ---- activities ----------------------------------------------------

    public static WorkflowActivity Http(
        string url,
        string method = "GET",
        string? body = null,
        IDictionary<string, string>? headers = null,
        params TransitionRule[] onSuccess)
    {
        var config = new HttpActivityConfig { Method = method, Url = url };

        if (body != null)
            config.Body = body;

        if (headers != null)
            foreach (var (key, value) in headers)
                config.Headers[key] = value;

        return Activity(new WorkflowActivity { Http = config }, onSuccess);
    }

    public static WorkflowActivity Timer(TimeSpan duration, params TransitionRule[] onSuccess)
    {
        return Activity(
            new WorkflowActivity { Timer = new TimerActivityConfig { Duration = Duration.FromTimeSpan(duration) } },
            onSuccess);
    }

    public static WorkflowActivity SubWorkflow(
        string workflowKey,
        string startActivity,
        Struct? input = null,
        bool detached = false,
        params TransitionRule[] onSuccess)
    {
        var config = new WorkflowActivityConfig
        {
            Workflow = workflowKey,
            StartActivity = startActivity,
            Detached = detached
        };

        if (input != null)
            config.Input = input;

        return Activity(new WorkflowActivity { Workflow = config }, onSuccess);
    }

    public static WorkflowActivity Promise(
        string mode,
        int requiredCount,
        PromiseBranch[] branches,
        params TransitionRule[] onSuccess)
    {
        var config = new PromiseActivityConfig { Mode = mode, RequiredCount = requiredCount };
        config.Branches.AddRange(branches);

        return Activity(new WorkflowActivity { Promise = config }, onSuccess);
    }

    public static PromiseBranch Branch(
        string name,
        string targetActivity,
        Struct? input = null,
        string? condition = null,
        string? forEachCollection = null,
        string forEachAlias = "item")
    {
        var target = new TransitionTarget { Name = targetActivity };

        if (input != null)
            target.Input = input;

        var branch = new PromiseBranch { Name = name, Target = target };

        if (condition != null)
            branch.Condition = condition;

        if (forEachCollection != null)
            branch.ForEach = new ForEachConfig { Collection = forEachCollection, Alias = forEachAlias };

        return branch;
    }

    // ---- transitions ---------------------------------------------------

    public static TransitionRule Goto(string target, Struct? input = null, string? condition = null)
    {
        var transitionTarget = new TransitionTarget { Name = target };

        if (input != null)
            transitionTarget.Input = input;

        var rule = new TransitionRule { Transition = transitionTarget };

        if (condition != null)
            rule.Condition = condition;

        return rule;
    }

    public static TransitionRule Result(Struct value, string? condition = null)
    {
        var rule = new TransitionRule { Result = value };

        if (condition != null)
            rule.Condition = condition;

        return rule;
    }

    // ---- values --------------------------------------------------------

    public static Struct Fields(params (string Key, object? Value)[] fields)
    {
        var result = new Struct();

        foreach (var (key, value) in fields)
            result.Fields[key] = ToValue(value);

        return result;
    }

    public static Value ToValue(object? value)
    {
        return value switch
        {
            null => Value.ForNull(),
            string s => Value.ForString(s),
            bool b => Value.ForBool(b),
            int i => Value.ForNumber(i),
            double d => Value.ForNumber(d),
            Struct s => Value.ForStruct(s),
            object[] items => Value.ForList(items.Select(ToValue).ToArray()),
            _ => throw new NotSupportedException($"No Value mapping for {value.GetType().Name}")
        };
    }

    private static WorkflowActivity Activity(WorkflowActivity activity, TransitionRule[] onSuccess)
    {
        activity.OnSuccess.AddRange(onSuccess);
        return activity;
    }
}
