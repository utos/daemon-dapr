using Common.Workflow;

namespace Execution.Domain.Schemas;

/// <summary>
/// Which declaration is being applied. It decides whether declared defaults are filled: an
/// inbound boundary fills, an outbound one never does.
/// <para>
/// The names match the <c>boundary</c> field of the spec's <c>conformance/schema/</c> cases, and
/// the lowercase spelling is what reaches an author in <c>error.details.schema.boundary</c>.
/// </para>
/// </summary>
public enum SchemaBoundary
{
    /// <summary>An activity's declared <c>schema.input</c>. Fills defaults.</summary>
    Input,

    /// <summary>A workflow's declared <c>spec.env</c>. Fills defaults.</summary>
    Env,

    /// <summary>A workflow's declared <c>spec.output</c>. Never fills.</summary>
    Output,

    /// <summary>A workflow's declared <c>spec.emits</c>. Never fills.</summary>
    Emits
}

/// <summary>
/// One failing location, as <c>api/docs/workflow-schemas.md</c> § What a failure reports defines
/// it: JSON Schema 2020-12's <em>basic</em> output narrowed to three fields.
/// </summary>
/// <param name="InstanceLocation">
/// A JSON Pointer into the value, rooted at the value itself — <c>""</c> is the whole value.
/// </param>
/// <param name="Keyword">
/// The keyword that failed: <c>required</c>, <c>type</c>, <c>maximum</c>, <c>format</c>,
/// <c>unevaluatedProperties</c>. Derivable from the last segment of <see cref="KeywordLocation"/>
/// and named anyway, so nothing has to parse a pointer to branch on it.
/// </param>
/// <param name="KeywordLocation">A JSON Pointer into the schema.</param>
public sealed record SchemaFailure(string InstanceLocation, string Keyword, string KeywordLocation);

/// <summary>
/// What applying a declaration to a value produced.
/// </summary>
/// <param name="Failures">
/// <strong>Every</strong> failure, not the first — so fixing one problem does not reveal the
/// next. Capped at <see cref="SchemaValidation.MaxFailures"/>, with <paramref name="Truncated"/>
/// carrying what was dropped, so a pathological payload cannot produce a pathological error.
/// </param>
/// <param name="Value">
/// The value after declared defaults were filled, at an inbound boundary; the value unchanged
/// otherwise. This is what must be <em>recorded</em>, so that a replay sees exactly what the run
/// saw and an operator reading an execution back sees the same.
/// </param>
public sealed record SchemaValidationResult(
    bool IsValid,
    IReadOnlyList<SchemaFailure> Failures,
    int Truncated,
    ValueMap Value)
{
    public static SchemaValidationResult Valid(ValueMap value) => new(true, [], 0, value);
}

/// <summary>
/// Applies a workflow's declared schemas to the data crossing a boundary —
/// <c>UTOS-H101</c>–<c>UTOS-H107</c> of <c>api/docs/workflow-schemas.md</c>.
/// <para>
/// The port lives in the domain for the same reason <see cref="Templating.ITemplateEngine"/>'s
/// does: whether a value satisfies its declaration is part of the interpretation decision, not an
/// infrastructure detail. The 2020-12 evaluator behind it is not, and stays outside.
/// </para>
/// <para>
/// Implementations must be <strong>pure and deterministic</strong> given the schema and the
/// value. Validation runs inside an activity that is already running, and its outcome is recorded
/// like any other activity result, so a replay must not re-derive a different answer.
/// </para>
/// </summary>
public interface ISchemaValidator
{
    /// <summary>
    /// Applies <paramref name="schema"/> to <paramref name="value"/>.
    /// </summary>
    /// <param name="schema">
    /// A JSON Schema 2020-12 document, already checked well-formed at load by
    /// <c>UTOS-H0##</c>. A null schema is the empty schema and everything satisfies it.
    /// </param>
    /// <exception cref="SchemaBudgetExceededException">
    /// Evaluation exceeded its time budget — <c>UTOS-H107</c>.
    /// </exception>
    SchemaValidationResult Validate(ValueMap? schema, ValueMap value, SchemaBoundary boundary);
}

/// <summary>Limits and codes shared by every boundary.</summary>
public static class SchemaValidation
{
    /// <summary>
    /// A cap on reported failures, so a pathological payload cannot produce a pathological error.
    /// The spec fixes this number rather than leaving it to implementations, because a caller
    /// comparing two daemons' reports would otherwise see different lists for one value.
    /// </summary>
    public const int MaxFailures = 100;

    public const string RunInput = "UTOS-H101";
    public const string RunEnv = "UTOS-H102";
    public const string TransitionInput = "UTOS-H103";
    public const string InvocationInput = "UTOS-H104";
    public const string Result = "UTOS-H105";
    public const string EmittedValue = "UTOS-H106";
    public const string BudgetExceeded = "UTOS-H107";
}

/// <summary><c>UTOS-H107</c> — evaluating a schema exceeded its budget.</summary>
public sealed class SchemaBudgetExceededException(string message) : Exception(message);
