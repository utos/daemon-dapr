using Execution.Features.Models;
using Mediator;

namespace Execution.Features.Queries;

public record ListExecutionsQuery : IQuery<List<ExecutionSummary>>;
