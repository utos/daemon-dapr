using System.Text.Json.Serialization;
using Common.Infrastructure.Serialization;
using Dapr.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ResolvePromiseInput(
    Guid ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput);

public sealed record ResolvePromiseOutput(
    PromiseMode Mode,
    int RequiredCount,
    List<PromiseBranchResolution> Branches);

/// <summary>
/// One branch to start. It carries no execution id: the executor mints those from its
/// replay-safe source once this activity's result is recorded.
/// </summary>
public sealed record PromiseBranchResolution(
    string BranchName,
    string WorkflowKey,
    string StartActivity,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Input);

/// <summary>
/// Durable boundary around <see cref="WorkflowInterpreter.ExpandPromise"/>. Deliberately
/// side-effect free — persisting each branch's execution context happens in the executor,
/// after it has assigned the ids those contexts are keyed by.
/// </summary>
public class ResolvePromiseActivity(
    IExecutionContextStore contextStore,
    WorkflowInterpreter interpreter)
    : WorkflowActivity<ResolvePromiseInput, ResolvePromiseOutput>
{
    public override async Task<ResolvePromiseOutput> RunAsync(
        WorkflowActivityContext context, ResolvePromiseInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);
        var session = WorkflowSession.From(executionContext, input.WorkflowKey);

        var expansion = interpreter.ExpandPromise(session, input.ActivityName, input.ActivityInput);

        var branches = expansion.Branches
            .Select(b => new PromiseBranchResolution(b.BranchName, b.WorkflowKey, b.StartActivity, b.Input))
            .ToList();

        return new ResolvePromiseOutput(expansion.Mode, expansion.RequiredCount, branches);
    }
}
