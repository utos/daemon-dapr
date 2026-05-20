using Mediator;
using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace Execution.Features.Commands;

public record ScheduleExecutionCommand(
    WorkflowBundle Bundle,
    Dictionary<string, object> Input,
    string StartActivity) : ICommand<ScheduleExecutionResult>;

public record ScheduleExecutionResult(Guid Id, ExecutionStatus Status);
