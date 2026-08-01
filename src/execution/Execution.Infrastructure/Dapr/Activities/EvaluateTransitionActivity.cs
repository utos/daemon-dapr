using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Common.Workflow;
using Dapr.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

/// <summary>
/// Durable boundary around <see cref="WorkflowInterpreter.Advance"/>. The decision itself is
/// domain logic; this exists so its result is recorded in the orchestration history once
/// rather than recomputed on every replay.
/// </summary>
public class EvaluateTransitionActivity(
    IExecutionContextStore contextStore,
    WorkflowInterpreter interpreter)
    : WorkflowActivity<EvaluateTransitionInput, EvaluateTransitionOutput>
{
    public override async Task<EvaluateTransitionOutput> RunAsync(
        WorkflowActivityContext context, EvaluateTransitionInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);
        var session = WorkflowSession.From(executionContext, input.WorkflowKey);

        var decision = interpreter.Advance(
            session, input.ActivityName, input.ActivityInput, input.ActivityOutput);

        return decision switch
        {
            TransitionDecision.Goto go => new EvaluateTransitionOutput(go.ActivityName, go.Input, null),
            TransitionDecision.Return result => new EvaluateTransitionOutput(
                ReservedKeywords.End, null, result.Value),
            _ => new EvaluateTransitionOutput(ReservedKeywords.End, null, null)
        };
    }
}

public sealed record EvaluateTransitionInput(
    ExecutionId ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ActivityOutput);

public sealed record EvaluateTransitionOutput(
    string? NextActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? MappedInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ReturnValue);
