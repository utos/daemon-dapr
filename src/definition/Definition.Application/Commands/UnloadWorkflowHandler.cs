using Definition.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Definition.Application.Commands;

public class UnloadWorkflowHandler(
    IWorkflowDefinitionStore store,
    ILogger<UnloadWorkflowHandler> logger)
    : ICommandHandler<UnloadWorkflowCommand, int>
{
    public ValueTask<int> Handle(UnloadWorkflowCommand command, CancellationToken ct)
    {
        var removed = store.Remove(command.Reference);

        logger.LogInformation("Unloaded {RemovedCount} workflow definition(s) for '{WorkflowName}'",
            removed, command.Reference.Name);

        return ValueTask.FromResult(removed);
    }
}
