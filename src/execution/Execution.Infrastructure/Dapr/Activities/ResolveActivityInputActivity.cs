using Common.SeedWork;
using System.Text.Json.Serialization;
using Common.Workflow.Serialization;
using Dapr.Workflow;
using Execution.Domain.Interpreter;
using Execution.Domain.Schemas;
using Execution.Domain.Services;
using Execution.Domain.Templating;
using Common.Workflow;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record ResolveActivityInputInput(
    ExecutionId ExecutionId,
    string WorkflowKey,
    string ActivityName,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? ActivityInput);

public sealed record ResolveActivityInputOutput(
    ActivityKind Kind,
    // HTTP fields (null when not HTTP)
    string? Url,
    string? HttpMethod,
    string? Body,
    Dictionary<string, string>? Headers,
    // Workflow call fields (null when not workflow)
    string? SubWorkflowKey,
    string? SubStartActivity,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? WorkflowInput,
    // True when the sub-workflow is fire-and-forget: the executor starts it as an independent
    // execution and does not await it. False for HTTP and awaited sub-workflows.
    bool Detached = false,
    // How long a timer waits, resolved (null when not a timer). Recorded here, in the
    // orchestration history, which is what makes a replay read the same deadline rather than
    // computing a new one from a later clock or a re-rendered template.
    TimeSpan? Duration = null,
    // UTOS-H104: the invocation input did not satisfy the callee's declared input. Carried as
    // data rather than thrown, because an exception crossing the Dapr boundary arrives flattened
    // to a message — the driver re-raises it so the code and the details survive.
    RaisedError? Error = null);

/// <summary>
/// Durable boundary around <see cref="WorkflowInterpreter.ResolveInput"/>, flattening its
/// result into the shape the executor passes on to the handler that does the work.
/// </summary>
public class ResolveActivityInputActivity(
    IExecutionContextStore contextStore,
    WorkflowInterpreter interpreter)
    : WorkflowActivity<ResolveActivityInputInput, ResolveActivityInputOutput>
{
    public override async Task<ResolveActivityInputOutput> RunAsync(
        WorkflowActivityContext context, ResolveActivityInputInput input)
    {
        var executionContext = await contextStore.LoadContextAsync(input.ExecutionId);
        var session = WorkflowSession.From(executionContext, input.WorkflowKey);

        // Drawn here, inside the activity, so a replay reads this result rather than drawing
        // again — see EvaluateTransitionActivity.
        ResolvedActivityInput resolved;
        try
        {
            resolved = interpreter.ResolveInput(
                session, input.ActivityName, input.ActivityInput, EvaluationMoment.Capture());
        }
        catch (SchemaValidationFailedException schema)
        {
            // UTOS-H104, returned rather than thrown: a Dapr activity's exception reaches the
            // driver flattened to a message, so a thrown one would lose its code and its failing
            // locations and be reported as the generic EXECUTION_FAILED. The driver re-raises
            // this as a coded failure of *this activity*, which is what makes H104 the one
            // run-time schema boundary an on_failure rule may catch.
            return new ResolveActivityInputOutput(
                ActivityKind.Workflow, null, null, null, null, null, null, null,
                Error: new RaisedError(schema.Code, schema.Message, schema.ToDetails()));
        }

        return resolved switch
        {
            ResolvedActivityInput.Http http => new ResolveActivityInputOutput(
                ActivityKind.Http,
                http.Url, http.Method, http.Body, http.Headers,
                null, null, null),

            ResolvedActivityInput.SubWorkflow sub => new ResolveActivityInputOutput(
                ActivityKind.Workflow,
                null, null, null, null,
                sub.WorkflowKey, sub.StartActivity, sub.Input, sub.Detached),

            ResolvedActivityInput.Timer timer => new ResolveActivityInputOutput(
                ActivityKind.Timer,
                null, null, null, null,
                null, null, null,
                Duration: timer.Duration),

            _ => throw new NotSupportedException(
                $"Unsupported resolved activity input '{resolved.GetType().Name}'")
        };
    }
}
