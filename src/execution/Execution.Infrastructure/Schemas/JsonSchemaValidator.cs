using System.Collections.Concurrent;
using System.Text.Json;
using Common.Workflow;
using Execution.Domain.Schemas;
using Json.Schema;

namespace Execution.Infrastructure.Schemas;

/// <summary>
/// The JSON Schema 2020-12 evaluator behind <see cref="ISchemaValidator"/>, implementing
/// <c>UTOS-H101</c>–<c>UTOS-H107</c> of <c>api/docs/workflow-schemas.md</c>.
/// <para>
/// Pure and deterministic given the schema and the value, which the port requires: validation
/// runs inside an activity that is already running and its outcome is recorded, so a replay must
/// not re-derive a different answer.
/// </para>
/// </summary>
public sealed class JsonSchemaValidator(SchemaValidatorOptions? options = null) : ISchemaValidator
{
    private readonly SchemaValidatorOptions _options = options ?? new SchemaValidatorOptions();

    /// <summary>
    /// Compiled schemas, keyed by their canonical JSON. A bundle's schemas are fixed for the life
    /// of a run and a transition may cross one on every step, so parsing per evaluation would pay
    /// the same cost repeatedly. Keyed by text rather than by reference because the bundle is
    /// deserialized afresh in each activity, so no two evaluations share a <see cref="ValueMap"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, JsonSchema> _compiled = new(StringComparer.Ordinal);

    public SchemaValidationResult Validate(ValueMap? schema, ValueMap value, SchemaBoundary boundary)
    {
        // No declaration is the empty schema, which anything the wire can carry satisfies. This
        // is the path every workflow written before schemas existed takes.
        if (schema is null || schema.Count == 0) return SchemaValidationResult.Valid(value);

        // Filled first, so that what is validated is what is recorded. A default cannot itself be
        // the thing that fails — UTOS-H008 checked it against its own schema at load.
        var effective = FillsDefaults(boundary) ? Defaults.Fill(schema, value) : value;

        var schemaJson = Serialize(schema);
        var valueJson = Serialize(effective);

        JsonSchema compiled;
        try
        {
            // FromText, never JsonSerializer.Deserialize<JsonSchema>: the latter is the
            // reflective overload, and although the daemon has no AOT constraint the CLI links
            // the same rules, so one spelling is kept everywhere.
            compiled = _compiled.GetOrAdd(schemaJson, static text => JsonSchema.FromText(text));
        }
        catch (JsonException e)
        {
            // UTOS-H002 rejects this at load, so reaching here means a bundle got in by another
            // route. Failing the boundary is right: the declaration cannot be applied.
            throw new SchemaBudgetExceededException("The declared schema could not be parsed: " + e.Message);
        }

        var started = DateTimeOffset.UtcNow;

        EvaluationResults results;
        using (var document = JsonDocument.Parse(valueJson))
        {
            results = compiled.Evaluate(document.RootElement, new EvaluationOptions
            {
                // Hierarchical, never List: a flat result cannot distinguish an aggregate from a
                // leaf, nor a failing branch of a *successful* anyOf from a real failure. See
                // Leaves below.
                OutputFormat = OutputFormat.Hierarchical,

                // Formats are asserted here, not annotated. 2020-12 treats `format` as an
                // annotation unless a validator opts in, which would make a declared `uuid`
                // check nothing.
                RequireFormatValidation = true
            });
        }

        var elapsed = DateTimeOffset.UtcNow - started;
        if (elapsed > _options.Budget)
        {
            // UTOS-H107. Detection after the fact rather than interruption — see
            // SchemaValidatorOptions.Budget for what that does and does not buy.
            throw new SchemaBudgetExceededException(
                $"Evaluating the declared schema took {elapsed.TotalMilliseconds:F0}ms, "
                + $"over the budget of {_options.Budget.TotalMilliseconds:F0}ms.");
        }

        var failures = new List<SchemaFailure>();
        if (!results.IsValid) Leaves(results, failures);

        // The safe-integer bound is ours, not 2020-12's: stock `integer` accepts 1e100, and a
        // schema that accepted one would declare a value no expression could ever read
        // (UTOS-E104).
        SafeIntegers.Check(schema, effective, failures);

        if (failures.Count == 0) return SchemaValidationResult.Valid(effective);

        var truncated = 0;
        if (failures.Count > SchemaValidation.MaxFailures)
        {
            truncated = failures.Count - SchemaValidation.MaxFailures;
            failures.RemoveRange(SchemaValidation.MaxFailures, truncated);
        }

        return new SchemaValidationResult(false, failures, truncated, effective);
    }

    /// <summary>An inbound boundary fills declared defaults; an outbound one never does.</summary>
    private static bool FillsDefaults(SchemaBoundary boundary) =>
        boundary is SchemaBoundary.Input or SchemaBoundary.Env;

    /// <summary>
    /// The failing locations a reader can act on: the leaves of the <em>invalid</em> subtree.
    /// <para>
    /// Two things have to be skipped, and a flat output format cannot express either. An invalid
    /// node with invalid children is an aggregate, so every real failure is also reported at its
    /// applicator and at the root. And a failing branch of a <em>successful</em> <c>anyOf</c> is
    /// not a failure at all — <c>{ "type": ["string", "null"] }</c>, which is what
    /// <c>nullable</c> compiles to, always fails one arm. Descending only through invalid parents
    /// drops both.
    /// </para>
    /// </summary>
    private static void Leaves(EvaluationResults node, List<SchemaFailure> failures)
    {
        var hasInvalidChild = false;

        if (node.Details is not null)
        {
            foreach (var child in node.Details)
            {
                if (child.IsValid) continue;
                hasInvalidChild = true;
                Leaves(child, failures);
            }
        }

        var instance = node.InstanceLocation.ToString();
        var keywordLocation = node.EvaluationPath.ToString();

        // A node can be an aggregate *and* fail on its own account: an object whose `properties`
        // reported a bad child can also be missing a `required` one. So record this node's own
        // assertion failures whether or not it has invalid children, and only treat applicator
        // keywords — `properties`, `allOf`, `$ref` — as the aggregates they are.
        var recorded = false;
        if (node.Errors is { Count: > 0 })
        {
            foreach (var error in node.Errors)
            {
                // An unevaluatedProperties failure reports an empty key, so the path is what
                // names it; every assertion reports its own keyword.
                var named = error.Key.Length > 0 ? error.Key : LastSegment(keywordLocation);
                if (!IsAssertion(named)) continue;

                Record(failures, instance, named, keywordLocation);
                recorded = true;
            }
        }

        // A leaf that named nothing still failed for a reason, and the path is the only thing
        // left that knows it.
        if (!hasInvalidChild && !recorded)
            Record(failures, instance, LastSegment(keywordLocation), keywordLocation);
    }

    /// <summary>
    /// Whether a keyword is an assertion — something that judged the instance — rather than an
    /// applicator that merely relayed a child's failure. Reporting an applicator would bury the
    /// one location a reader can act on under the chain of keywords that reached it.
    /// </summary>
    private static bool IsAssertion(string keyword) => keyword is
        "type" or "enum" or "const" or "required" or "dependentRequired"
        or "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf"
        or "minLength" or "maxLength" or "pattern" or "format"
        or "minItems" or "maxItems" or "uniqueItems" or "contains" or "minContains" or "maxContains"
        or "minProperties" or "maxProperties";

    // `unevaluatedProperties`, `additionalProperties` and `unevaluatedItems` are deliberately
    // absent. Each reports twice — once on the containing object, naming the applicator, and once
    // on the offending property, where the `false` subschema was applied — and the spec pins the
    // second: a typo'd key reported against "" says only that something, somewhere, was not
    // expected. The property-level report carries an empty keyword, so the leaf fallback above is
    // what names it, and leaving these out of the assertion set is what drops the container's
    // duplicate.

    /// <summary>
    /// One entry per failing (location, keyword). A composed schema can reach the same conclusion
    /// by more than one route, and the corpus asserts the set exactly.
    /// </summary>
    private static void Record(List<SchemaFailure> failures, string instance, string keyword, string keywordLocation)
    {
        if (keyword.Length == 0) return;
        if (failures.Any(f => f.InstanceLocation == instance && f.Keyword == keyword)) return;

        failures.Add(new SchemaFailure(instance, keyword, keywordLocation));
    }

    private static string LastSegment(string pointer)
    {
        if (pointer.Length == 0) return string.Empty;

        var index = pointer.LastIndexOf('/');
        var segment = index < 0 ? pointer : pointer[(index + 1)..];

        // A keyword location can end in an index — `/anyOf/1` — where the keyword is the segment
        // before it.
        if (segment.Length > 0 && segment.All(char.IsDigit) && index > 0)
            return LastSegment(pointer[..index]);

        return segment;
    }

    /// <summary>
    /// A <see cref="ValueMap"/> carries its own converter, writing a bare JSON object with real
    /// CLR leaves, so this needs no options and cannot produce <c>JsonElement</c> leaves.
    /// </summary>
    private static string Serialize(ValueMap value) => JsonSerializer.Serialize(value);
}

/// <summary>Knobs for <see cref="JsonSchemaValidator"/>.</summary>
public sealed class SchemaValidatorOptions
{
    /// <summary>
    /// The wall-clock budget for one evaluation — <c>UTOS-H107</c>.
    /// <para>
    /// This is checked <em>after</em> evaluation rather than interrupting it, which bounds what
    /// it is worth: it reports a pathological schema rather than preventing one from spending the
    /// time. What keeps that from mattering much is that the schema is not attacker-controlled —
    /// it is part of a bundle, already checked at load, where <c>UTOS-H010</c> refuses an
    /// unparseable <c>pattern</c> and <c>UTOS-H012</c> bounds pattern length and schema size. The
    /// residual risk is catastrophic backtracking on an adversarial <em>value</em>, and this
    /// turns that into a reported failure on the next check rather than a silent stall.
    /// </para>
    /// </summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);
}
