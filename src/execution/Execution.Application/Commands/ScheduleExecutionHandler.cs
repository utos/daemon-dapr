using Common.Workflow.Resolution;
using Execution.Application.Exceptions;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Resolves the reference and snapshots the content it names into the execution, then hands off
/// to <see cref="StartExecutionCommand"/>. Resolution is a contract, not a module: what answers
/// it is decided at composition, so starting a run does not depend on how — or by whom —
/// definitions are stored.
/// </summary>
public class ScheduleExecutionHandler(IWorkflowResolver resolver, IMediator mediator)
    : ICommandHandler<ScheduleExecutionCommand, ScheduleExecutionResult>
{
    public async ValueTask<ScheduleExecutionResult> Handle(
        ScheduleExecutionCommand command, CancellationToken ct)
    {
        var resolved = resolver.Resolve(command.Workflow)
                       ?? throw new WorkflowDefinitionNotFoundException(command.Workflow);

        return await mediator.Send(new StartExecutionCommand(
            resolved.Identity,
            resolved.Digest,
            resolved.Bundle,
            command.Input,
            command.StartActivity,
            command.Env), ct);
    }
}
