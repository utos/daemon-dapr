using Common.Workflow;
using Definition.Application.Exceptions;
using Definition.Domain;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Definition.Application.Commands;

public class LoadWorkflowHandler(
    IWorkflowDefinitionStore store,
    IValidator<WorkflowBundle> bundleValidator,
    ILogger<LoadWorkflowHandler> logger)
    : ICommandHandler<LoadWorkflowCommand, LoadedDefinition>
{
    public ValueTask<LoadedDefinition> Handle(LoadWorkflowCommand command, CancellationToken ct)
    {
        var validation = bundleValidator.Validate(command.Bundle);
        if (!validation.IsValid)
            throw new WorkflowBundleInvalidException(validation.ToString());

        var definition = store.Load(command.Bundle, command.Digest);

        logger.LogInformation("Loaded workflow definition {WorkflowIdentity} ({WorkflowDigest})",
            definition.Identity, definition.Digest);

        return ValueTask.FromResult(definition);
    }
}
