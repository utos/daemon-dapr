using Utos.Workflow.V1;

namespace Execution.Domain.Interpreter;

/// <summary>
/// One workflow out of a bundle, addressed by the key it is stored under. The structural
/// half of a run: enough to decide where to go next, but with no run-specific data, so the
/// navigator can use it without a template engine.
/// </summary>
public sealed record WorkflowGraph(Workflow Workflow, string WorkflowKey)
{
    public bool TryGetActivity(string activityName, out WorkflowActivity activity)
    {
        return Workflow.Spec.Activities.TryGetValue(activityName, out activity!);
    }

    /// <summary>
    /// The activity by that name, or <c>null</c> when the workflow does not define one.
    /// </summary>
    public WorkflowActivity? Find(string activityName)
    {
        return Workflow.Spec.Activities.GetValueOrDefault(activityName);
    }
}
