using Common.Workflow;
using Mediator;

namespace Definition.Application.Commands;

/// <summary>
/// Removes loaded definitions matching the reference. An omitted version removes every version
/// of that name; a set digest narrows removal to the single matching version.
/// </summary>
public record UnloadWorkflowCommand(WorkflowReference Reference) : ICommand<int>;
