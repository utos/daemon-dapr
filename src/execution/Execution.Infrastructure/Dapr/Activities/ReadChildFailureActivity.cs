using System.Text.Json.Serialization;
using Common.Constants;
using Common.SeedWork;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Output;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ReadChildFailureInput(ExecutionId ChildExecutionId);

/// <summary>What a child's stream says it failed with; the generic code when it says nothing.</summary>
public sealed record ReadChildFailureOutput(
    string Code,
    string Message,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Details);

/// <summary>
/// Recovers why an awaited child failed, from the one place the answer is recorded intact.
/// <para>
/// A child orchestration's failure reaches its caller as the runtime's <c>TaskFailedException</c>,
/// which flattens whatever was thrown to a type name and a message string — the same boundary that
/// makes <c>HttpActivityFailedException</c> necessary, one level up. The code and details an
/// authored <c>error</c> action gave the failure are gone by then. They are on the child's output
/// stream, though, as its terminal entry, written before the child's lifecycle event: so reading
/// that entry, in an activity whose result is recorded once, hands the caller the failure as it was
/// authored, replay-safe. A consuming caller does not need this — the child sends its terminal
/// message directly — but an awaiting one and a promise branch do.
/// </para>
/// </summary>
public class ReadChildFailureActivity(IExecutionOutputStore outputStore)
    : WorkflowActivity<ReadChildFailureInput, ReadChildFailureOutput>
{
    public override async Task<ReadChildFailureOutput> RunAsync(
        WorkflowActivityContext context, ReadChildFailureInput input)
    {
        var terminal = await outputStore.LastEntryAsync(input.ChildExecutionId);

        return terminal?.Entry is ExecutionErrorProduced error
            ? new ReadChildFailureOutput(error.Code, error.Message, error.Details)
            : new ReadChildFailureOutput(WorkflowErrorCode.ExecutionFailed, string.Empty, null);
    }
}
