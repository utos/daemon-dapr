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

            // A run recorded before output streams existed has no entry at all, and a run that
            // ended with no value — a bare `return`, or its rules running out — has an entry with
            // none. Both report no result: spec 0.0.16 says an empty `result` leaves
            // GetExecution.result unset, and the status already says the run completed.
            if (terminal?.Entry is ExecutionResultProduced { Result: not null } produced)
                details.Result = AsValueMap(produced.Result);
        }

        // The same read for a failure's details: an `error` action's details struct is on the
        // terminal entry and nowhere else, since the read model projects the code and message
        // only. Read only when the stream says there were any, so a plain failure costs nothing.
        if (details.Status == ExecutionStatus.Failed)
        {
            var terminal = await outputStore.LastEntryAsync(query.ExecutionId, ct);

            if (terminal?.Entry is ExecutionErrorProduced { Details: not null } error)
                details.ErrorDetails = new ValueMap(
                    error.Details.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }

        return details;
    }

    /// <summary>
    /// A result is a rendered <see cref="ValueMap"/> by construction, but it comes back through
    /// the event serializer as a plain dictionary. Anything else means the entry on disk is not
    /// what wrote it, which is worth reporting rather than quietly showing as empty.
    /// </summary>
    private static ValueMap AsValueMap(object value) => value switch
    {
        ValueMap map => map,
        IReadOnlyDictionary<string, object?> fields => new ValueMap(fields),
        IDictionary<string, object?> fields => new ValueMap(fields.AsReadOnly()),

        _ => throw new InvalidOperationException(
            $"An execution result must be a structure, not {value.GetType().Name}.")
    };
}
