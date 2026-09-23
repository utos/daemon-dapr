using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Schemas;
using Execution.Domain.Services;
using Execution.Domain.Templating;

namespace Execution.Infrastructure.Dapr.Activities;

/// <summary>
/// Durable boundary around <see cref="WorkflowInterpreter.Advance"/>. The decision itself is
/// domain logic; this exists so its result is recorded in the orchestration history once
/// rather than recomputed on every replay.
/// <para>
/// That once-ness is also what makes <see cref="EvaluationMoment.Capture"/> correct here: the
/// instant and the seed every expression of this decision reads are drawn as the activity runs,
/// and a replay reads the recorded result rather than drawing again.
/// </para>
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

        TransitionDecision decision;
        try
        {
            decision = interpreter.Advance(
                session, input.ActivityName, input.ActivityInput, input.ActivityOutput, input.Outcome,
                input.Response, input.Error, EvaluationMoment.Capture());
        }
        catch (SchemaValidationFailedException schema)
        {
            // UTOS-H103, H105, H106. Reported the same way an authored `error` action is, so the
            // run fails with the code the spec gives it and `error.details.schema` carries every
            // failing location — rather than as an unrecognised exception, which the driver would
            // report as the generic EXECUTION_FAILED and strip of its details.
            //
            // Deliberately NOT routed through on_failure. A transition is the act of leaving, and
            // a result that violates spec.output is not something an author opts out of: the
            // caller that trusted the contract is the one that would otherwise carry the damage.
            return new EvaluateTransitionOutput(
                null, null, null,
                Error: new RaisedError(schema.Code, schema.Message, schema.ToDetails()));
        }

        // Leaving is only meaningful on the emitted path — everywhere else there is no
        // subscription to leave — so the driver is told the conclusion rather than the two facts
        // it would otherwise have to combine.
        var leaves = input.Outcome == ActivityOutcomeKind.Emitted
                     && decision is TransitionDecision.Goto or TransitionDecision.Return or TransitionDecision.Fail;

        // Where the run goes. The exit alone: since spec 0.20 a rule's effect is a separate
        // question, so it is carried separately rather than folded into one of these arms.
        var output = decision switch
        {
            TransitionDecision.Goto go => new EvaluateTransitionOutput(
                go.ActivityName, go.Input, null, LeavesSubscription: leaves),

            // Back for the next value on the same subscription. Identical in shape to a Goto and
            // deliberately not folded into it: this is the one that must *not* cancel the producer.
            TransitionDecision.Resume resume => new EvaluateTransitionOutput(
                resume.ActivityName, resume.Input, null),

            // No next activity: the path ends, with the value or — a bare `return` — without one.
            TransitionDecision.Return result => new EvaluateTransitionOutput(
                null, null, result.Value, LeavesSubscription: leaves),

            // Nothing handled the failure, so the driver must rethrow rather than continue.
            // Matched explicitly: folding it into the fallback below would turn an unhandled
            // failure into a normally completed run.
            TransitionDecision.Propagate => new EvaluateTransitionOutput(
                null, null, null, Unhandled: true),

            // The rule said fail, and said why. Rendered already; the driver raises it as-is.
            TransitionDecision.Fail fail => new EvaluateTransitionOutput(
                null, null, null, LeavesSubscription: leaves,
                Error: new RaisedError(fail.Code, fail.Message, fail.Details)),

            // The rules ran out on an ordinary path: the run is done.
            _ => new EvaluateTransitionOutput(null, null, null)
        };

        // What the rule does on the way out, if anything. Both rides alongside the exit rather
        // than replacing it: the driver performs the effect and then takes the exit, which is how
        // `emit` then `return`, or `workflow.call` then `transition`, become one evaluation and
        // one transition.
        output = decision.Effect switch
        {
            DecisionEffect.Emit emit => output with { EmittedValue = emit.Value },

            DecisionEffect.Dispatch dispatch => output with
            {
                Dispatch = new DispatchTarget(
                    dispatch.Workflow, dispatch.StartActivity, dispatch.Input)
            },

            _ => output
        };

        return output with
        {
            MatchedList = decision.Match?.List,
            MatchedRule = decision.Match?.Number ?? 0,
            RuleCount = decision.Match?.Of ?? 0,
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
    // Plain ints, strings, a string map and bytes — no converter needed, unlike ActivityOutput,
    // which carries arbitrary workflow values.
    HttpResponseContext? Response = null,
    WorkflowErrorContext? Error = null);

/// <param name="NextActivityName">
/// Where the run goes. Null means the path ends here — the rules ran out, or a rule returned —
/// unless <paramref name="Dispatch"/>, <paramref name="Unhandled"/> or <paramref name="Error"/>
/// says otherwise, each of which the driver checks first. There is no keyword for "end" any more:
/// the absence of a next activity is the end.
/// </param>
/// <param name="EmittedValue">
/// What an <c>emit</c> effect produced, to be appended to the execution's output stream before the
/// run takes this rule's exit. Null means the rule did not emit: a rendered value is always a
/// dictionary — empty at the least — so null is unambiguous.
/// </param>
/// <param name="Unhandled">
/// No <c>on_failure</c> rule matched, so the driver must rethrow rather than continue. Carried as
/// a flag rather than as a null <paramref name="NextActivityName"/> because "go nowhere" already
/// means the run finished normally, and the two outcomes must not be confusable.
/// </param>
/// <param name="Dispatch">
/// The document a <c>workflow.call</c> effect runs, to termination, before the run takes this
/// rule's exit. Not exclusive with <paramref name="NextActivityName"/>: the dispatched document
/// decides nothing about where this workflow goes — that is the whole point of it being a document
/// — so the exit beside it is the consumer's own, and a rule with no exit resumes.
/// </param>
/// <param name="MatchedList">
/// Which rule list decided — <c>onSuccess</c>, <c>onFailure</c> or <c>onEmitted</c> — with
/// <paramref name="MatchedRule"/> its 1-based position and <paramref name="RuleCount"/> the list's
/// length. Null when no rule matched. Recorded here, in the orchestration history, so a replay
/// logs the same rule it matched the first time. Optional fields, so history written before them
/// still reads.
/// </param>
/// <param name="LeavesSubscription">
/// The consumer is done consuming: this value's rule was a <c>transition</c>, a <c>result</c> or
/// an <c>error</c>, so the producer is cancelled now rather than whenever this run happens to end
/// — which, for the long-running consumer these actions exist to serve, could be much later or
/// never.
/// </param>
/// <param name="Error">
/// A rule's <c>error</c> action matched: the run fails with exactly this. Distinct from
/// <paramref name="Unhandled"/>, which rethrows the failure that was being handled; this one
/// replaces it with what the author wrote.
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
    bool Unhandled = false,
    bool LeavesSubscription = false,
    string? MatchedList = null,
    int MatchedRule = 0,
    int RuleCount = 0,
    RaisedError? Error = null);

/// <summary>Which document an emitted value is handed to, and where it starts.</summary>
public sealed record DispatchTarget(
    string Workflow,
    string StartActivity,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Input);

/// <summary>An <c>error</c> action, rendered: what the run will report.</summary>
public sealed record RaisedError(
    string Code,
    string Message,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Details);
