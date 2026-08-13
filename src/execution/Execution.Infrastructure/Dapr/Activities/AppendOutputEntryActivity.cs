using System.Text.Json.Serialization;
using Common.Constants;
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

public sealed record AppendOutputEntryInput(
    ExecutionId ExecutionId,
    OutputEntryKind Kind,
    // Carries the same converter the created event's input does: without it the value rehydrates
    // as JsonElement on the far side of the Dapr hop and every template reading it breaks.
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Value = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Durable boundary around an append to an execution's output stream, modelled on
/// <see cref="WriteLifecycleEventActivity"/> and for the same reason: the orchestrator cannot do
/// I/O, and recording the append in orchestration history is what makes it happen once rather
/// than again on every replay.
/// </summary>
public class AppendOutputEntryActivity(IExecutionOutputStore store)
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
                occurredAt),

            _ => throw new ArgumentOutOfRangeException(
                nameof(input), input.Kind, "Unknown output entry kind")
        };

        await store.AppendAsync(input.ExecutionId, entry);

        return new object();
    }
}
