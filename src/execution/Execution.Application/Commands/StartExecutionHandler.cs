using Common.SeedWork;
using Common.Workflow;
using Mediator;
using Execution.Domain.Aggregates;
using Execution.Domain.Schemas;

namespace Execution.Application.Commands;

public class StartExecutionHandler(IEventStoreRepository eventStore, ISchemaValidator schemaValidator)
    : ICommandHandler<StartExecutionCommand, ScheduleExecutionResult>
{
    public async ValueTask<ScheduleExecutionResult> Handle(
        StartExecutionCommand command, CancellationToken ct)
    {
        var (input, env) = Check(command);

        var workflowExecution = new WorkflowExecution(
            command.Bundle, input, command.StartActivity, env, command.ExecutionId,
            command.Digest);
        await eventStore.SaveAsync<WorkflowExecution, ExecutionId>(workflowExecution);
        return new ScheduleExecutionResult(workflowExecution.Id, workflowExecution.Status);
    }

    /// <summary>
    /// <c>UTOS-H101</c> and <c>UTOS-H102</c>: the run input against the start activity's declared
    /// input, and the run's environment against <c>spec.env</c>.
    /// <para>
    /// Here rather than in a FluentValidation rule for a reason that is not stylistic: a
    /// validator cannot <em>mutate</em> the command, and these two boundaries fill declared
    /// defaults. What is recorded has to be the filled value, so that a replay sees exactly what
    /// the run saw and an operator reading the execution back sees the same — which means the
    /// filling must happen before <see cref="WorkflowExecution"/> is constructed, and that is
    /// this method's only chance.
    /// </para>
    /// <para>
    /// No run exists yet, so a failure is <c>INVALID_ARGUMENT</c> rather than a failed run: there
    /// is nothing to inspect afterwards and no partial work to undo.
    /// </para>
    /// </summary>
    private (Dictionary<string, object> Input, Dictionary<string, string> Env) Check(
        StartExecutionCommand command)
    {
        var workflow = command.Bundle.Entry;
        if (workflow is null) return (command.Input, command.Env);

        var input = command.Input;
        if (workflow.Spec.Activities.TryGetValue(command.StartActivity, out var start)
            && start.InputSchema is { } declaredInput)
        {
            var result = schemaValidator.Validate(
                declaredInput, AsValueMap(command.Input), SchemaBoundary.Input);

            if (!result.IsValid)
                throw new SchemaValidationFailedException(
                    SchemaValidation.RunInput, SchemaBoundary.Input, result);

            input = result.Value.Fields
                .Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!);
        }

        var env = command.Env;
        if (workflow.Spec.Env is { } declaredEnv)
        {
            var result = schemaValidator.Validate(
                declaredEnv,
                ValueMap.From(command.Env.Select(e => new KeyValuePair<string, object?>(e.Key, e.Value))),
                SchemaBoundary.Env);

            if (!result.IsValid)
                throw new SchemaValidationFailedException(
                    SchemaValidation.RunEnv, SchemaBoundary.Env, result);

            // Back to string-to-string, which is what env is on the wire and in scope. A declared
            // default is the only thing that can have been added, and spec.env admits only
            // string-typed properties (UTOS-H011), so nothing here can fail to be one.
            env = result.Value.Fields
                .Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!.ToString()!);
        }

        return (input, env);
    }

    private static ValueMap AsValueMap(Dictionary<string, object> value) =>
        ValueMap.From(value.Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)));
}
