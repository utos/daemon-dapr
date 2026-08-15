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
            session, input.ActivityName, input.ActivityInput, input.ActivityOutput, input.Emitted);

        return decision switch
        {
            TransitionDecision.Goto go => new EvaluateTransitionOutput(go.ActivityName, go.Input, null),

            TransitionDecision.Return result => new EvaluateTransitionOutput(
                ReservedKeywords.End, null, result.Value),

            // The emitted value rides alongside the target rather than becoming a step of its
            // own: the driver appends it and then continues, so one evaluation still yields one
            // transition.
            TransitionDecision.Emit emit => new EvaluateTransitionOutput(
                emit.ActivityName, emit.Input, null, emit.Value),

            _ => new EvaluateTransitionOutput(ReservedKeywords.End, null, null)
        };
    }
}

/// <param name="Emitted">
/// The output is a value a sub-workflow emitted rather than its result, so <c>on_emitted</c>
/// applies. Only the driver can tell the two apart — by value they are identical.
/// </param>
public sealed record EvaluateTransitionInput(
    ExecutionId ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ActivityOutput,
    bool Emitted = false);

/// <param name="EmittedValue">
/// What an <c>emit</c> action produced, to be appended to the execution's output stream before
/// the run continues to <paramref name="NextActivityName"/>. Null means the rule was not an emit:
/// a rendered value is always a dictionary — empty at the least — so null is unambiguous.
/// </param>
public sealed record EvaluateTransitionOutput(
    string? NextActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? MappedInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ReturnValue,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? EmittedValue = null);
