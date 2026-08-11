using Common.Workflow;
// Aliased rather than imported: Utos.Workflows.V1 also contains Workflow and WorkflowActivity,
// which would collide with the domain types this builder constructs.
using WorkflowDocument = Utos.Workflows.V1.WorkflowDocument;
using Execution.Domain.Interpreter;
using Execution.Infrastructure.Templating;
using Microsoft.Extensions.Logging.Abstractions;

namespace UnitTests.Support;

/// <summary>
/// Terse builders for the shapes the interpreter reads. Kept minimal on purpose — a test
/// should read as the workflow it describes, not as model assembly.
/// </summary>
internal static class Wf
{
    public static WorkflowGraph Graph(params (string Name, WorkflowActivity Activity)[] activities)
    {
        var workflow = new Workflow(
            WorkflowDocument.ApiVersion,
            WorkflowDocument.Kind,
            new WorkflowMetadata("test", "1.0.0"),
            new WorkflowSpec(activities.ToDictionary(a => a.Name, a => a.Activity)));

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
        return new WorkflowActivity(
            new HttpActivityConfig(
                method,
                url,
                body,
                headers == null ? null : new Dictionary<string, string>(headers)),
            onSuccess);
    }

    public static WorkflowActivity Timer(TimeSpan duration, params TransitionRule[] onSuccess)
    {
        return new WorkflowActivity(new TimerActivityConfig(duration), onSuccess);
    }

    public static WorkflowActivity SubWorkflow(
        string workflowKey,
        string startActivity,
        ValueMap? input = null,
        bool detached = false,
        params TransitionRule[] onSuccess)
    {
        return new WorkflowActivity(
            new SubWorkflowActivityConfig(workflowKey, startActivity, input, detached),
            onSuccess);
    }

    public static WorkflowActivity Promise(
        string mode,
        int requiredCount,
        PromiseBranch[] branches,
        params TransitionRule[] onSuccess)
    {
        return new WorkflowActivity(
            new PromiseActivityConfig(mode, requiredCount, branches),
            onSuccess);
    }

    public static PromiseBranch Branch(
        string name,
        string targetActivity,
        ValueMap? input = null,
        string? condition = null,
        string? forEachCollection = null,
        string forEachAlias = "item")
    {
        return new PromiseBranch(
            name,
            new TransitionTarget(targetActivity, input),
            condition,
            forEachCollection == null ? null : new ForEachConfig(forEachCollection, forEachAlias));
    }

    // ---- transitions ---------------------------------------------------

    public static TransitionRule Goto(string target, ValueMap? input = null, string? condition = null)
    {
        return TransitionRule.Goto(target, input, condition);
    }

    public static TransitionRule Result(ValueMap value, string? condition = null)
    {
        return TransitionRule.Returns(value, condition);
    }

    // ---- values --------------------------------------------------------

    public static ValueMap Fields(params (string Key, object? Value)[] fields)
    {
        return new ValueMap(fields.ToDictionary(f => f.Key, f => ToValue(f.Value)));
    }

    /// <summary>
    /// Normalizes a literal into the model's own value shape. Nested maps arrive as
    /// <see cref="ValueMap"/> so tests build the same thing mapping from the wire produces.
    /// </summary>
    public static object? ToValue(object? value)
    {
        return value switch
        {
            null => null,
            ValueMap map => map,
            object[] items => items.Select(ToValue).ToList(),
            _ => value
        };
    }
}
