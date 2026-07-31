using Definition.Domain;
using Execution.Application.Exceptions;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Resolves the reference against the definition store and snapshots the content into the
/// execution, then hands off to <see cref="StartExecutionCommand"/>.
/// </summary>
public class ScheduleExecutionHandler(IWorkflowDefinitionStore definitionStore, IMediator mediator)
    : ICommandHandler<ScheduleExecutionCommand, ScheduleExecutionResult>
{
    public async ValueTask<ScheduleExecutionResult> Handle(
        ScheduleExecutionCommand command, CancellationToken ct)
    {
        var definition = definitionStore.Resolve(command.Workflow)
                         ?? throw new WorkflowDefinitionNotFoundException(command.Workflow);

        return await mediator.Send(new StartExecutionCommand(
            definition.Identity,
            definition.Digest,
            definition.Bundle,
            command.Input,
            command.StartActivity,
            command.Env), ct);
    }
}
