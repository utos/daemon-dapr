using System.Text.Json.Serialization;
using Common.SeedWork;
using Common.Workflow.Serialization;

namespace Execution.Domain.Events;

/// <summary>
/// The run failed. <paramref name="Reason"/> is the message; <paramref name="Code"/> and
/// <paramref name="Details"/> are what an authored <c>error</c> action gave it, and null for a
/// failure the daemon raised itself, which reports the generic code. Both are optional nullable
/// fields added after the event's first shape, which is the one evolution that needs neither a
/// version bump nor an upcaster: an event written without them reads back with nulls.
/// </summary>
[EventType("utos.execution.failed")]
public record WorkflowExecutionFailedEvent(
    ExecutionId ExecutionId,
    string Reason,
    DateTime DateOccurred,
    string? Code = null,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Details = null) : IDomainEvent;
