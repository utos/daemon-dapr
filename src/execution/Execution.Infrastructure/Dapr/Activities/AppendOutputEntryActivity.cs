using System.Text.Json.Serialization;
using Common.Blobs;
using Common.Constants;
using Common.Workflow;
using Common.SeedWork;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Output;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

/// <summary>Which entry to append. The terminal kinds close the stream; there is exactly one.</summary>
public enum OutputEntryKind
{
    Value,
    Result,
    Error
}

/// <param name="ReachesRootOutput">
/// Whether this entry is going onto a <em>root</em> execution's output stream — the run tree's own,
/// rather than a child's. A blob that reaches it becomes durable, because what a run hands to a
/// client is what somebody asked for, while what a child hands its caller is still in flight.
/// <para>
/// Positional and required, where everything after it is optional, so that adding an append site
/// breaks the build until it has answered. Neither default is safe: <c>false</c> would silently lose
/// a blob a client asked for, and <c>true</c> would pin intermediates in storage for good.
/// </para>
/// </param>
public sealed record AppendOutputEntryInput(
    ExecutionId ExecutionId,
    OutputEntryKind Kind,
    bool ReachesRootOutput,
    // Carries the same converter the created event's input does: without it the value rehydrates
    // as JsonElement on the far side of the Dapr hop and every template reading it breaks.
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Value = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ErrorDetails = null);

/// <summary>
/// Durable boundary around an append to an execution's output stream, modelled on
/// <see cref="WriteLifecycleEventActivity"/> and for the same reason: the orchestrator cannot do
/// I/O, and recording the append in orchestration history is what makes it happen once rather
/// than again on every replay.
/// </summary>
public class AppendOutputEntryActivity(IExecutionOutputStore store, IBlobAccess blobs)
    : WorkflowActivity<AppendOutputEntryInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, AppendOutputEntryInput input)
    {
        // A wall clock rather than the orchestrator's replay-safe one: this runs inside an
        // activity, whose result is recorded once, so the timestamp cannot drift on replay.
        var occurredAt = DateTime.UtcNow;

        ExecutionOutputEntry entry = input.Kind switch
        {
            OutputEntryKind.Value =>
                new ExecutionValueEmitted(input.ExecutionId, input.Value, occurredAt),

            OutputEntryKind.Result =>
                new ExecutionResultProduced(input.ExecutionId, input.Value, occurredAt),

            OutputEntryKind.Error => new ExecutionErrorProduced(
                input.ExecutionId,
                input.ErrorCode ?? WorkflowErrorCode.ExecutionFailed,
                input.ErrorMessage ?? string.Empty,
                occurredAt,
                input.ErrorDetails),

            _ => throw new ArgumentOutOfRangeException(
                nameof(input), input.Kind, "Unknown output entry kind")
        };

        await store.AppendAsync(input.ExecutionId, entry);

        // Promotion, and after the append rather than before it. § Out of a run makes the append the
        // moment a blob reaches the stream, so promoting first would make a blob durable that a crash
        // then left out of any output — an object nothing refers to and nothing will remove. This way
        // round a crash leaves the entry present and the blob intermediate, and the sweep removes
        // bytes the client can still see a handle for. Both are wrong; only one of them is recoverable
        // by hand, and only one of them accumulates.
        //
        // Inside the activity, so the work happens once and is not repeated on every replay.
        if (input.ReachesRootOutput && input.Value is not null)
            await blobs.PromoteAsync(
                // A root execution is its own run tree, which is why the id serves as both here.
                BlobWalk.In(input.Value), input.ExecutionId, CancellationToken.None);

        return new object();
    }
}
