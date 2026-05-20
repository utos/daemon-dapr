using Execution.Features.Models;
using Execution.Features.Repositories;
using Mediator;

namespace Execution.Features.Queries;

public class ListExecutionsHandler(
    IWorkflowExecutionRepository executionRepository)
    : IQueryHandler<ListExecutionsQuery, List<ExecutionSummary>>
{
    public async ValueTask<List<ExecutionSummary>> Handle(
        ListExecutionsQuery query, CancellationToken ct)
    {
        return await executionRepository.ListExecutionsAsync(ct);
    }
}
