using Common.Workflow;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Schedules a run of a definition already loaded into the daemon's store. The reference is
/// resolved against that store here, in the application layer — the transport only translates
/// the request.
/// </summary>
public record ScheduleExecutionCommand(
    WorkflowReference Workflow,
    Dictionary<string, object> Input,
    string StartActivity,
    // Ambient config available to every activity via {{ env.x }}, unlike Input which
    // reaches only the start activity. Non-secret only.
    Dictionary<string, string> Env) : ICommand<ScheduleExecutionResult>;
