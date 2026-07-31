using Execution.Domain.Services;

namespace Execution.Domain.Interpreter;

/// <summary>
/// A workflow plus the run-specific data its templates read: the input the execution
/// started with and the run's ambient environment. Everything the interpreter needs to
/// resolve a value, and nothing that would require touching a store to get.
/// </summary>
public sealed record WorkflowSession(
    WorkflowGraph Graph,
    IReadOnlyDictionary<string, object> RunInput,
    IReadOnlyDictionary<string, string> Env)
{
    public static WorkflowSession From(Services.ExecutionContext context, string workflowKey)
    {
        if (!context.Bundle.Workflows.TryGetValue(workflowKey, out var workflow))
            throw new InvalidOperationException(
                $"Workflow '{workflowKey}' not found in bundle for execution {context.ExecutionId}");

        return new WorkflowSession(
            new WorkflowGraph(workflow, workflowKey),
            context.Input,
            context.Env);
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
