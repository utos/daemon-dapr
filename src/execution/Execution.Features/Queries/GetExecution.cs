using Common.SeedWork;
using Execution.Features.Models;
using Mediator;

namespace Execution.Features.Queries;

public record GetExecutionQuery(ExecutionId ExecutionId) : IQuery<ExecutionDetails?>;
