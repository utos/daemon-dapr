using Common.Workflow;
using Execution.Domain.Services;

namespace Execution.Domain.Interpreter;

/// <summary>
/// A workflow plus the run-specific data its templates read: the input the execution
/// started with and the run's ambient environment. Everything the interpreter needs to
/// resolve a value, and nothing that would require touching a store to get.
/// </summary>
/// <param name="Bundle">
/// The whole bundle this workflow came from, where the caller had one. <see cref="Graph"/> is a
/// single document and that is all most decisions need, but an <em>invocation</em> hands its
/// input to another document's start activity, so checking it against what that activity declares
/// (<c>UTOS-H104</c>) means reading the callee. Optional, because a caller that builds a session
/// by hand has no bundle and an invocation it cannot resolve is simply not checked.
/// </param>
public sealed record WorkflowSession(
    WorkflowGraph Graph,
    IReadOnlyDictionary<string, object> RunInput,
    IReadOnlyDictionary<string, string> Env,
    WorkflowBundle? Bundle = null)
{
    public static WorkflowSession From(Services.ExecutionContext context, string workflowKey)
    {
        if (!context.Bundle.Workflows.TryGetValue(workflowKey, out var workflow))
            throw new InvalidOperationException(
                $"Workflow '{workflowKey}' not found in bundle for execution {context.ExecutionId}");

        return new WorkflowSession(
            new WorkflowGraph(workflow, workflowKey),
            context.Input,
            context.Env,
            context.Bundle);
    }

    /// <summary>
    /// The input schema the activity <paramref name="startActivity"/> of document
    /// <paramref name="workflowKey"/> declares, or null where there is nothing to check against —
    /// no bundle, no such document, no such activity, or no declaration.
    /// </summary>
    public ValueMap? DeclaredInputOf(string workflowKey, string startActivity)
    {
        if (Bundle is null) return null;
        if (!Bundle.Workflows.TryGetValue(workflowKey, out var workflow)) return null;

        return workflow.Spec.Activities.TryGetValue(startActivity, out var activity)
            ? activity.InputSchema
            : null;
    }

    /// <summary>
    /// The input an activity sees: what the previous transition mapped, or the execution's
    /// own input when this is the first activity on the path.
    /// </summary>
    public IReadOnlyDictionary<string, object> InputFor(IReadOnlyDictionary<string, object>? activityInput)
    {
        return activityInput ?? RunInput;
    }
}
