using Execution.Application.Models;
using Execution.Application.Repositories;
using Mediator;

namespace Execution.Application.Queries;

public class ListExecutionsHandler(
    IWorkflowExecutionRepository executionRepository)
    : IQueryHandler<ListExecutionsQuery, List<ExecutionSummary>>
{
    public async ValueTask<List<ExecutionSummary>> Handle(
        ListExecutionsQuery query, CancellationToken ct)
    {
        return await executionRepository.ListExecutionsAsync(query.Workflow, ct);
    }
}
