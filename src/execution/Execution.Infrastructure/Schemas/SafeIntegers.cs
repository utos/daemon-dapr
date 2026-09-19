using Common.Workflow;
using Execution.Domain.Schemas;

namespace Execution.Infrastructure.Schemas;

/// <summary>
/// The one refinement this dialect makes to stock 2020-12 that an evaluator will not make on its
/// own: <strong><c>integer</c> means an integer the expression language can hold</strong>, at most
/// ±2⁵³, which is the same bound <c>UTOS-E104</c> draws on a value entering scope.
/// <para>
/// Stock <c>integer</c> is "any number with a zero fractional part", so <c>1e100</c> passes it.
/// A schema that accepted one would be declaring a value no expression could ever read, and an
/// id that came back changed would be worse than an error — such ids travel as strings.
/// </para>
/// <para>
/// Reported as <c>type</c>, not as <c>maximum</c>: the bound is part of what <c>integer</c> means
/// here, not a separate constraint the author could have written. Injecting real
/// <c>minimum</c>/<c>maximum</c> keywords into the schema instead would have been less code and
/// the wrong diagnostic.
/// </para>
/// </summary>
internal static class SafeIntegers
{
    /// <summary>±(2⁵³), as <c>template-expressions.md</c> § Numbers draws it.</summary>
    private const double Limit = 9007199254740992d;

    /// <summary>
    /// Walks schema and value together, adding a <c>type</c> failure wherever a declared
    /// <c>integer</c> holds a number outside the safe range.
    /// <para>
    /// Covers the shapes the source format's short form produces — <c>properties</c>,
    /// <c>items</c>, and a <c>$ref</c> into the same document's <c>$defs</c>. It deliberately
    /// does <em>not</em> descend <c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>/<c>if</c>, which only
    /// raw JSON Schema can reach: knowing which arm applies means re-running the evaluation, and
    /// the backstop that actually prevents such a value being read is <c>UTOS-E104</c> in the
    /// expression engine, which refuses it whatever the schema said.
    /// </para>
    /// </summary>
    internal static void Check(ValueMap schema, ValueMap value, List<SchemaFailure> failures)
        => CheckObject(schema, value, string.Empty, string.Empty, schema, failures, depth: 0);

    private static void CheckObject(ValueMap schema, ValueMap value, string instancePath,
        string schemaPath, ValueMap root, List<SchemaFailure> failures, int depth)
    {
        if (depth > 32) return;

        var resolved = Resolve(schema, root, ref schemaPath, depth);
        if (resolved is null) return;

        var properties = Defaults.Properties(resolved);
        if (properties is null) return;

        var propertiesPath = schemaPath + "/properties";

        foreach (var (name, propertySchemaValue) in properties)
        {
            if (propertySchemaValue is not ValueMap propertySchema) continue;
            if (!value.TryGetValue(name, out var propertyValue)) continue;

            CheckValue(propertySchema, propertyValue, instancePath + "/" + Escape(name),
                propertiesPath + "/" + Escape(name), root, failures, depth + 1);
        }
    }

    private static void CheckValue(ValueMap schema, object? value, string instancePath,
        string schemaPath, ValueMap root, List<SchemaFailure> failures, int depth)
    {
        if (depth > 32) return;

        var resolved = Resolve(schema, root, ref schemaPath, depth);
        if (resolved is null) return;

        switch (value)
        {
            case ValueMap nested:
                CheckObject(resolved, nested, instancePath, schemaPath, root, failures, depth);
                break;

            case System.Collections.IEnumerable list and not string:
                if (!resolved.TryGetValue("items", out var items) || items is not ValueMap itemSchema) break;

                var index = 0;
                foreach (var element in list)
                {
                    CheckValue(itemSchema, element, instancePath + "/" + index,
                        schemaPath + "/items", root, failures, depth + 1);
                    index++;
                }

                break;

            default:
                if (!DeclaresInteger(resolved)) break;
                if (!IsOutsideSafeRange(value)) break;

                failures.Add(new SchemaFailure(instancePath, "type", schemaPath + "/type"));
                break;
        }
    }

    /// <summary>Follows a <c>$ref</c> into this document's own <c>$defs</c>, once.</summary>
    private static ValueMap? Resolve(ValueMap schema, ValueMap root, ref string schemaPath, int depth)
    {
        if (depth > 32) return null;
        if (!schema.TryGetValue("$ref", out var reference) || reference is not string pointer) return schema;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return schema;

        ValueMap? current = root;
        var path = string.Empty;

        foreach (var raw in pointer[2..].Split('/'))
        {
            if (current is null) return null;

            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            path += "/" + segment;

            current = current.TryGetValue(segment, out var next) ? next as ValueMap : null;
        }

        if (current is null) return null;

        schemaPath = path;
        return current;
    }

    private static bool DeclaresInteger(ValueMap schema)
    {
        if (!schema.TryGetValue("type", out var type)) return false;

        return type switch
        {
            string single => single == "integer",

            // `nullable` compiles to a type union, so an integer can arrive as ["integer","null"].
            System.Collections.IEnumerable many and not string =>
                many.Cast<object?>().Any(t => t as string == "integer"),

            _ => false
        };
    }

    private static bool IsOutsideSafeRange(object? value)
    {
        var number = value switch
        {
            int i => i,
            long l => l,
            double d => d,
            decimal m => (double)m,
            _ => (double?)null
        };

        if (number is not { } n) return false;

        // A fractional value is stock `integer`'s business and is already reported as `type`;
        // reporting it twice would break the exact-set match the corpus asserts.
        if (double.IsNaN(n) || double.IsInfinity(n)) return true;
        if (n != Math.Floor(n)) return false;

        return Math.Abs(n) > Limit;
    }

    private static string Escape(string segment) =>
        segment.Replace("~", "~0").Replace("/", "~1");
}
