using Common.SeedWork;
using Mediator;

namespace Execution.Application.Commands;

/// <summary>
/// Stops a running execution. The run reaches the terminal status Cancelled, and the durable
/// orchestration behind it is torn down.
/// </summary>
/// <param name="Reason">
/// Why, for whoever reads the record later. Optional — "cancelled, no reason given" is an
/// ordinary thing for a caller to mean.
/// </param>
public record CancelExecutionCommand(ExecutionId ExecutionId, string? Reason = null)
    : ICommand<CancelExecutionResult>;

public record CancelExecutionResult;
