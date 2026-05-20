using Execution.Features.Models;
using Mediator;

namespace Execution.Features.Queries;

public record GetExecutionQuery(Guid ExecutionId) : IQuery<ExecutionDetails?>;
