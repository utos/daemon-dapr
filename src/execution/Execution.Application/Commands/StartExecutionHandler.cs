using Common.SeedWork;
using Mediator;
using Execution.Domain.Aggregates;

namespace Execution.Application.Commands;

public class StartExecutionHandler(IEventStoreRepository eventStore)
    : ICommandHandler<StartExecutionCommand, ScheduleExecutionResult>
{
    public async ValueTask<ScheduleExecutionResult> Handle(
        StartExecutionCommand command, CancellationToken ct)
    {
        var workflowExecution = new WorkflowExecution(
            command.Bundle, command.Input, command.StartActivity, command.Env, command.ExecutionId,
            command.Digest);
        await eventStore.SaveAsync<WorkflowExecution, ExecutionId>(workflowExecution);
        return new ScheduleExecutionResult(workflowExecution.Id, workflowExecution.Status);
    }
}
