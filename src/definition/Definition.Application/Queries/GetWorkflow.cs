using Common.Workflow;
using Definition.Application.Exceptions;
using Definition.Domain;
using Mediator;

namespace Definition.Application.Queries;

public record GetWorkflowQuery(WorkflowReference Reference) : IQuery<LoadedDefinition>;

public class GetWorkflowHandler(IWorkflowDefinitionStore store)
    : IQueryHandler<GetWorkflowQuery, LoadedDefinition>
{
    public ValueTask<LoadedDefinition> Handle(GetWorkflowQuery query, CancellationToken ct)
    {
        var definition = store.Resolve(query.Reference)
                         ?? throw new WorkflowDefinitionNotFoundException(query.Reference);

        return ValueTask.FromResult(definition);
    }
}
