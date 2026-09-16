using Definition.Domain;
using Mediator;

namespace Definition.Application.Queries;

public record ListWorkflowsQuery : IQuery<IReadOnlyList<LoadedDefinition>>;

public class ListWorkflowsHandler(IWorkflowDefinitionStore store)
    : IQueryHandler<ListWorkflowsQuery, IReadOnlyList<LoadedDefinition>>
{
    public ValueTask<IReadOnlyList<LoadedDefinition>> Handle(ListWorkflowsQuery query, CancellationToken ct) =>
        ValueTask.FromResult(store.List());
}
