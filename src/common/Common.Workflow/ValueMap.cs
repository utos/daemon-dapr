using System.Text.Json.Serialization;
using Common.Workflow.Serialization;

namespace Common.Workflow;

/// <summary>
/// An authored structured value — the shape a transform, a result, or a sub-workflow input is
/// written in. Leaves are JSON primitives as real CLR types (<c>string</c>, <c>int</c>,
/// <c>long</c>, <c>double</c>, <c>bool</c>, <c>null</c>), nested <see cref="ValueMap"/>s, or
/// <c>List&lt;object?&gt;</c>.
///
/// This is deliberately the same representation the interpreter already uses for run input, env
/// and activity output, so a rendered value needs no conversion to become the next activity's
/// input. It replaces <c>google.protobuf.Struct</c>, which required a hop through <c>ToDict()</c>
/// at every boundary.
///
/// The converter is declared on the type rather than left to the caller's
/// <see cref="System.Text.Json.JsonSerializerOptions"/>, so the "real CLR values, never
/// <see cref="System.Text.Json.JsonElement"/>" guarantee holds wherever a ValueMap is
/// serialized — event stream, EF column or Dapr payload alike. It writes as a bare JSON
/// object, with no wrapper property.
/// </summary>
[JsonConverter(typeof(ValueMapJsonConverter))]
public sealed class ValueMap(IReadOnlyDictionary<string, object?> fields)
{
    public static ValueMap Empty { get; } = new(new Dictionary<string, object?>());

    public IReadOnlyDictionary<string, object?> Fields { get; } = fields;

    public int Count => Fields.Count;

    public object? this[string key] => Fields[key];

    public bool TryGetValue(string key, out object? value) => Fields.TryGetValue(key, out value);

    public static ValueMap From(IEnumerable<KeyValuePair<string, object?>> fields) =>
        new(new Dictionary<string, object?>(fields));

    /// <summary>
    /// A mutable copy. The interpreter hands rendered values on to activity inputs, which are
    /// <c>Dictionary&lt;string, object&gt;</c> by the time they cross a Dapr boundary.
    /// </summary>
    public Dictionary<string, object> ToDictionary() =>
        Fields.Where(kvp => kvp.Value != null)
              .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
}
