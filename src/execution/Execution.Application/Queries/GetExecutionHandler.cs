using Common.Workflow;
using Execution.Application.Models;
using Execution.Application.Repositories;
using Execution.Domain.Aggregates;
using Execution.Domain.Output;
using Execution.Domain.Services;
using Mediator;

namespace Execution.Application.Queries;

public class GetExecutionHandler(
    IWorkflowExecutionRepository executionRepository,
    IExecutionOutputStore outputStore)
    : IQueryHandler<GetExecutionQuery, ExecutionDetails?>
{
    public async ValueTask<ExecutionDetails?> Handle(
        GetExecutionQuery query, CancellationToken ct)
    {
        var details = await executionRepository.GetExecutionDetailsAsync(query.ExecutionId, ct);

        if (details is null)
            return null;

        // Read back from the output stream rather than kept in the read model. The stream is
        // already where a run's result lives, and a copy in a projection would be a second thing
        // to keep in step — one the lifecycle event it is built from does not even carry.
        //
        // Safe to read as soon as the status says Completed: the driver appends the terminal
        // entry *before* the lifecycle event, so by the time a projection has seen the run
        // finish, the entry is there. One backwards read of one event, and only for a run that
        // has something to report.
        if (details.Status == ExecutionStatus.Completed)
        {
            var terminal = await outputStore.LastEntryAsync(query.ExecutionId, ct);

            // A run recorded before output streams existed has no entry at all. It reports no
            // result rather than failing the read.
            if (terminal?.Entry is ExecutionResultProduced produced)
                details.Result = AsValueMap(produced.Result);
        }

        return details;
    }

    /// <summary>
    /// A result is a rendered <see cref="ValueMap"/> by construction, but it comes back through
    /// the event serializer as a plain dictionary. Anything else means the entry on disk is not
    /// what wrote it, which is worth reporting rather than quietly showing as empty.
    /// </summary>
    private static ValueMap AsValueMap(object? value) => value switch
    {
        null => ValueMap.Empty,
        ValueMap map => map,
        IReadOnlyDictionary<string, object?> fields => new ValueMap(fields),
        IDictionary<string, object?> fields => new ValueMap(fields.AsReadOnly()),

        _ => throw new InvalidOperationException(
            $"An execution result must be a structure, not {value.GetType().Name}.")
    };
}
