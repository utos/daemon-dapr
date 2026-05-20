using Execution.Features.Models;
using Execution.Features.Repositories;
using Mediator;

namespace Execution.Features.Queries;

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
