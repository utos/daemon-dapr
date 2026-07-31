using Definition.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Definition.Application.Commands;

public class LoadWorkflowHandler(
    IWorkflowDefinitionStore store,
    ILogger<LoadWorkflowHandler> logger)
    : ICommandHandler<LoadWorkflowCommand, LoadedDefinition>
{
    public ValueTask<LoadedDefinition> Handle(LoadWorkflowCommand command, CancellationToken ct)
    {
        // The bundle's shape is checked by LoadWorkflowCommandValidator in the pipeline.
        var definition = store.Load(command.Bundle, command.Digest);

        logger.LogInformation("Loaded workflow definition {WorkflowIdentity} ({WorkflowDigest})",
            definition.Identity, definition.Digest);

        return ValueTask.FromResult(definition);
    }
}
