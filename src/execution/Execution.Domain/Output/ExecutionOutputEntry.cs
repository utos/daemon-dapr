using System.Text.Json.Serialization;
using Common.SeedWork;
using Common.Workflow.Serialization;

namespace Execution.Domain.Output;

/// <summary>
/// One entry in an execution's output stream — the values it produced, in order, ending in
/// exactly one terminal entry. See <c>api/docs/execution-output-stream.md</c>.
/// <para>
/// Deliberately named <c>utos.output.*</c> rather than <c>utos.execution.*</c>. The persistent
/// subscription behind the read model filters on <c>EventTypeFilter.Prefix("utos.execution.")</c>,
/// so anything named under that prefix is fed to every projection and event handler
/// automatically. Output entries are data a workflow produced, read by cursor on demand; routing
/// them through the projection pipeline would do work nobody asked for on every emitted value.
/// The separation used to rest on this file's namespace, which meant a routine move of the file
/// would have changed what the daemon does; it now rests on the names below, and
/// <c>EventTypeRegistryTests</c> asserts they stay outside the prefix.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExecutionValueEmitted), "value")]
[JsonDerivedType(typeof(ExecutionResultProduced), "result")]
[JsonDerivedType(typeof(ExecutionErrorProduced), "error")]
public abstract record ExecutionOutputEntry(ExecutionId ExecutionId, DateTime DateOccurred) : IDomainEvent
{
    /// <summary>Terminal entries are always last, and there is exactly one.</summary>
    [JsonIgnore]
    public abstract bool IsTerminal { get; }
}

/// <summary>A value the workflow emitted with an <c>emit</c> transition action.</summary>
[EventType("utos.output.value")]
public sealed record ExecutionValueEmitted(
    ExecutionId ExecutionId,
    // Without the converter this rehydrates as JsonElement and every downstream template breaks,
    // exactly as it would for a created event's input.
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Value,
    DateTime DateOccurred) : ExecutionOutputEntry(ExecutionId, DateOccurred)
{
    public override bool IsTerminal => false;
}

/// <summary>Terminal: the workflow returned this value.</summary>
[EventType("utos.output.result")]
public sealed record ExecutionResultProduced(
    ExecutionId ExecutionId,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Result,
    DateTime DateOccurred) : ExecutionOutputEntry(ExecutionId, DateOccurred)
{
    public override bool IsTerminal => true;
}

/// <summary>
/// Terminal: the workflow failed, or was cancelled. The three fields of the
/// <c>WorkflowError</c> a client reads; <paramref name="Details"/> is an optional nullable field
/// added after the first shape, so an entry written without it reads back with null.
/// </summary>
[EventType("utos.output.error")]
public sealed record ExecutionErrorProduced(
    ExecutionId ExecutionId,
    string Code,
    string Message,
    DateTime DateOccurred,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Details = null) : ExecutionOutputEntry(ExecutionId, DateOccurred)
{
    public override bool IsTerminal => true;
}
