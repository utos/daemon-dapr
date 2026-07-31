using Common.SeedWork;
using Execution.Application.Models;
using Mediator;

namespace Execution.Application.Queries;

public record GetExecutionQuery(ExecutionId ExecutionId) : IQuery<ExecutionDetails?>;
