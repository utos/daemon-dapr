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
    /// <summary>
    /// The identity <see cref="Graph"/> builds under, and so what a dispatch of the document
    /// under test resolves to. Bundles carry canonical identities — <c>self</c> is a source-format
    /// word the CLI has already resolved — so this is what a daemon-side fixture writes.
    /// </summary>
    public const string SelfKey = "test:1.0.0";

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

    public static ExecutionLimits Limits(int continueAsNewAfter = 100)
    {
        return new ExecutionLimits(continueAsNewAfter);
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
        bool spawn = false,
        params TransitionRule[] onSuccess)
    {
        SubWorkflowActivityConfig config = spawn
            ? new SpawnActivityConfig(workflowKey, startActivity, input)
            : new CallActivityConfig(workflowKey, startActivity, input);

        return new WorkflowActivity(config, onSuccess);
    }

    /// <summary>
    /// A <c>workflow.call</c> that consumes what its child emits. Separate from
    /// <see cref="SubWorkflow"/> because <c>on_emitted</c> selects a different rule list entirely,
    /// and only a call — never a spawn — can carry one.
    /// </summary>
    public static WorkflowActivity ConsumingCall(
        string workflowKey,
        string startActivity,
        EmissionRule[] onEmitted,
        params TransitionRule[] onSuccess)
    {
        return new WorkflowActivity(
            new CallActivityConfig(workflowKey, startActivity, null, onEmitted),
            onSuccess);
    }

    public static WorkflowActivity Promise(
        PromiseMode mode,
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
        string startActivity,
        ValueMap? input = null,
        string? condition = null,
        string? forEachCollection = null,
        string forEachAlias = "item",
        string workflow = SelfKey)
    {
        return new PromiseBranch(
            name,
            workflow,
            startActivity,
            input,
            condition,
            forEachCollection == null ? null : new ForEachConfig(forEachCollection, forEachAlias));
    }

    /// <summary>An on_emitted rule: hand the value to a document and take the next one.</summary>
    public static EmissionRule Dispatch(
        string startActivity,
        ValueMap? input = null,
        string? condition = null,
        string workflow = SelfKey)
    {
        return EmissionRule.Handles(workflow, startActivity, input, condition);
    }

    /// <summary>An on_emitted rule: stop consuming and continue at an activity in this workflow.</summary>
    public static EmissionRule EmittedGoto(
        string name, ValueMap? input = null, string? condition = null) =>
        EmissionRule.Goto(name, input, condition);

    /// <summary>An on_emitted rule: stop consuming and end this run with a result.</summary>
    public static EmissionRule EmittedResult(ValueMap result, string? condition = null) =>
        EmissionRule.Returns(result, condition);

    // ---- transitions ---------------------------------------------------

    public static TransitionRule Goto(string target, ValueMap? input = null, string? condition = null)
    {
        return TransitionRule.Goto(target, input, condition);
    }

    public static TransitionRule Result(ValueMap value, string? condition = null)
    {
        return TransitionRule.Returns(value, condition);
    }

    public static TransitionRule Emit(
        ValueMap value, string target, ValueMap? input = null, string? condition = null)
    {
        return TransitionRule.Emits(value, target, input, condition);
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
