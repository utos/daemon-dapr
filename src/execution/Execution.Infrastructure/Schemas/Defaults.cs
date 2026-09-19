using Common.Workflow;

namespace Execution.Infrastructure.Schemas;

/// <summary>
/// Filling a declared <c>default</c> — the one place a schema touches data, and
/// <c>api/docs/workflow-schemas.md</c> § Defaults bounds it deliberately.
/// </summary>
internal static class Defaults
{
    /// <summary>
    /// Returns <paramref name="value"/> with every absent optional property that declares a
    /// <c>default</c> filled in.
    /// <para>
    /// <strong>Absent, not falsy, and not null.</strong> A property explicitly holding
    /// <c>null</c> is present and keeps its null — that is the same distinction <c>?</c> and
    /// <c>nullable</c> draw, and collapsing it would make a schema rewrite a value the author
    /// deliberately sent.
    /// </para>
    /// </summary>
    internal static ValueMap Fill(ValueMap schema, ValueMap value)
    {
        var properties = Properties(schema);
        if (properties is null) return value;

        Dictionary<string, object?>? filled = null;

        foreach (var (name, propertySchema) in properties)
        {
            if (propertySchema is not ValueMap declaration) continue;

            if (!value.TryGetValue(name, out var existing))
            {
                if (!declaration.TryGetValue("default", out var fallback)) continue;

                filled ??= new Dictionary<string, object?>(value.Fields);
                filled[name] = fallback;
                continue;
            }

            // Present and itself an object: recurse, so a default nested one level down is filled
            // too. A property that is absent altogether is filled from its own `default` above
            // and not synthesised from its children's — a declaration that says nothing about
            // whether the object should exist must not conjure one.
            if (existing is ValueMap nested)
            {
                var nestedFilled = Fill(declaration, nested);
                if (!ReferenceEquals(nestedFilled, nested))
                {
                    filled ??= new Dictionary<string, object?>(value.Fields);
                    filled[name] = nestedFilled;
                }
            }
        }

        return filled is null ? value : ValueMap.From(filled);
    }

    /// <summary>The <c>properties</c> map of an object schema, or null where it declares none.</summary>
    internal static IReadOnlyDictionary<string, object?>? Properties(ValueMap schema) =>
        schema.TryGetValue("properties", out var properties) && properties is ValueMap map
            ? map.Fields
            : null;
}
