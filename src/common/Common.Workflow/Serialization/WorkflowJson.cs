using System.Text.Json;

namespace Common.Workflow.Serialization;

/// <summary>
/// The options every path that reads or writes workflow values must use. There were three
/// near-identical copies of this and two paths that passed no options at all, which is how the
/// same value came back as a real CLR type down one route and a <see cref="JsonElement"/> down
/// another.
/// </summary>
public static class WorkflowJson
{
    /// <summary>
    /// Carries <see cref="ObjectDictionaryJsonConverter"/> for <c>object</c>-typed leaves, and
    /// deliberately not <see cref="StringObjectDictionaryJsonConverter"/>: that one is chosen by
    /// a property annotation, and naming it here would make it re-enter itself when serializing
    /// the very dictionary type it handles.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new ObjectDictionaryJsonConverter() }
    };
}
