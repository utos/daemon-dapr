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
            session, input.ActivityName, input.ActivityInput, input.ActivityOutput, input.Outcome,
            input.Response, input.Error);

        return decision switch
        {
            TransitionDecision.Goto go => new EvaluateTransitionOutput(go.ActivityName, go.Input, null),

            TransitionDecision.Return result => new EvaluateTransitionOutput(
                ReservedKeywords.End, null, result.Value),

            // The emitted value rides alongside the target rather than becoming a step of its
            // own: the driver appends it and then continues, so one evaluation still yields one
            // transition.
            TransitionDecision.Emit emit => new EvaluateTransitionOutput(
                emit.ActivityName, emit.Input, null, EmittedValue: emit.Value),

            // A handler to run before the next value is taken. Carries no next activity: the
            // driver goes back to the call activity once the dispatched execution ends, and
            // deciding that here would mean naming the call activity twice.
            TransitionDecision.Dispatch dispatch => new EvaluateTransitionOutput(
                null, null, null,
                Dispatch: new DispatchTarget(
                    dispatch.Workflow, dispatch.StartActivity, dispatch.Input)),

            // Nothing handled the failure, so the driver must rethrow rather than continue.
            // Matched explicitly: folding it into the fallback below would turn an unhandled
            // failure into a normally completed run.
            TransitionDecision.Propagate => new EvaluateTransitionOutput(
                null, null, null, Unhandled: true),

            _ => new EvaluateTransitionOutput(ReservedKeywords.End, null, null)
        };
    }
}

/// <param name="Outcome">
/// Which rule list applies. Only the driver can tell these apart — an emitted value and a result
/// are identical by value, and a failure carries neither.
/// </param>
public sealed record EvaluateTransitionInput(
    ExecutionId ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ActivityOutput,
    ActivityOutcomeKind Outcome = ActivityOutcomeKind.Succeeded,
    // Plain ints, strings and a string map — no converter needed, unlike ActivityOutput, which
    // carries arbitrary workflow values.
    HttpResponseContext? Response = null,
    WorkflowErrorContext? Error = null);

/// <param name="EmittedValue">
/// What an <c>emit</c> action produced, to be appended to the execution's output stream before
/// the run continues to <paramref name="NextActivityName"/>. Null means the rule was not an emit:
/// a rendered value is always a dictionary — empty at the least — so null is unambiguous.
/// </param>
/// <param name="Unhandled">
/// No <c>on_failure</c> rule matched, so the driver must rethrow rather than continue. Carried as
/// a flag rather than as a null <paramref name="NextActivityName"/> because "go nowhere" already
/// means the run finished normally, and the two outcomes must not be confusable.
/// </param>
/// <param name="Dispatch">
/// The document to run for the emitted value just consumed. Set only on the emitted path, and
/// mutually exclusive with <paramref name="NextActivityName"/> — a dispatch leaves this workflow,
/// so there is no activity here to go to next.
/// </param>
public sealed record EvaluateTransitionOutput(
    string? NextActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? MappedInput,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? ReturnValue,
    DispatchTarget? Dispatch = null,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? EmittedValue = null,
    bool Unhandled = false);

/// <summary>Which document an emitted value is handed to, and where it starts.</summary>
public sealed record DispatchTarget(
    string Workflow,
    string StartActivity,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Input);
