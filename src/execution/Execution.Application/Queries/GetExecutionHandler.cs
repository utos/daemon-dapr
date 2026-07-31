using Execution.Application.Models;
using Execution.Application.Repositories;
using Mediator;

namespace Execution.Application.Queries;

public class GetExecutionHandler(
    IWorkflowExecutionRepository executionRepository)
    : IQueryHandler<GetExecutionQuery, ExecutionDetails?>
{
    public async ValueTask<ExecutionDetails?> Handle(
        GetExecutionQuery query, CancellationToken ct)
    {
        return await executionRepository.GetExecutionDetailsAsync(query.ExecutionId, ct);
    }
}
