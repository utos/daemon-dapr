using Common.SeedWork;
using Mediator;

namespace Execution.Features.Commands;

/// <summary>
/// Deletes a terminal execution's record — its read-model row and event streams — together
/// with the records of its non-detached sub-workflow executions. Detached sub-workflows are
/// independent top-level executions and are not affected.
/// </summary>
public record DeleteExecutionCommand(ExecutionId ExecutionId) : ICommand<DeleteExecutionResult>;

public record DeleteExecutionResult;
