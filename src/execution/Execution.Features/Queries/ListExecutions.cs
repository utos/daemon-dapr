using Execution.Features.Models;
using Mediator;
using Common.Workflow;

namespace Execution.Features.Queries;

public record ListExecutionsQuery(WorkflowReference? Workflow = null) : IQuery<List<ExecutionSummary>>;
