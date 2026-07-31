using Execution.Features.Models;
using Mediator;
using WorkflowReference = Utos.Daemon.V1.WorkflowReference;

namespace Execution.Features.Queries;

public record ListExecutionsQuery(WorkflowReference? Workflow = null) : IQuery<List<ExecutionSummary>>;
