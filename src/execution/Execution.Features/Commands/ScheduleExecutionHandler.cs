using Common.SeedWork;
using Mediator;
using Execution.Domain.Aggregates;

namespace Execution.Features.Commands;

public class ScheduleExecutionHandler(IEventStoreRepository eventStore)
    : ICommandHandler<ScheduleExecutionCommand, ScheduleExecutionResult>
{
    public async ValueTask<ScheduleExecutionResult> Handle(
        ScheduleExecutionCommand command, CancellationToken ct)
    {
        var workflowExecution = new WorkflowExecution(
            command.Bundle, command.Input, command.StartActivity, command.Env, command.ExecutionId,
            command.Digest);
        await eventStore.SaveAsync(workflowExecution);
        return new ScheduleExecutionResult(workflowExecution.Id, workflowExecution.Status);
    }
}
