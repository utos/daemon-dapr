using System.Text.Json.Serialization;
using Common.SeedWork;
using Common.Workflow.Serialization;

namespace Execution.Domain.Output;

/// <summary>
/// One entry in an execution's output stream — the values it produced, in order, ending in
/// exactly one terminal entry. See <c>api/docs/execution-output-stream.md</c>.
/// <para>
/// Deliberately **not** in <c>Execution.Domain.Events</c>. The persistent subscription behind the
/// read model filters on <c>EventTypeFilter.Prefix("Execution.Domain.Events")</c>, so anything
/// declared there is fed to every projection and event handler automatically. Output entries are
/// data a workflow produced, read by cursor on demand; routing them through the projection
/// pipeline would do work nobody asked for on every emitted value.
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
public sealed record ExecutionResultProduced(
    ExecutionId ExecutionId,
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Result,
    DateTime DateOccurred) : ExecutionOutputEntry(ExecutionId, DateOccurred)
{
    public override bool IsTerminal => true;
}

/// <summary>Terminal: the workflow failed, or was cancelled.</summary>
public sealed record ExecutionErrorProduced(
    ExecutionId ExecutionId,
    string Code,
    string Message,
    DateTime DateOccurred) : ExecutionOutputEntry(ExecutionId, DateOccurred)
{
    public override bool IsTerminal => true;
}
