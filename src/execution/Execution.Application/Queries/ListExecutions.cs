using Execution.Application.Models;
using Mediator;
using Common.Workflow;

namespace Execution.Application.Queries;

public record ListExecutionsQuery(WorkflowReference? Workflow = null) : IQuery<List<ExecutionSummary>>;
