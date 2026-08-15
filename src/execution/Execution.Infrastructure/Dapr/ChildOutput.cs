using System.Text.Json.Serialization;
using Common.Workflow.Serialization;

namespace Execution.Infrastructure.Dapr;

/// <summary>
/// The channels a sub-workflow hands its output to its caller on, and the caller acknowledges
/// back.
/// </summary>
public static class OutputEvents
{
    /// <summary>
    /// One channel per call activity rather than one per run. A workflow may consume output at
    /// more than one activity, and a shared name would let a value from one child satisfy the
    /// other's wait — silently pairing a value with the wrong handler.
    /// </summary>
    public static string For(string activityName) => $"child-output:{activityName}";

    /// <summary>
    /// The acknowledgement, back to the child. Constant because it is addressed to a single child
    /// instance, and a child has exactly one caller.
    /// </summary>
    public const string Consumed = "output-consumed";
}

public enum ChildOutputKind
{
    Value,
    Result,
    Error
}

/// <summary>
/// What a gated child sends its caller: one message per emitted value, then exactly one terminal
/// message. Mirrors the shape of the output stream, because it is the same sequence of entries —
/// the stream is the durable record, this is the delivery.
/// </summary>
public sealed record ChildOutputMessage(
    ChildOutputKind Kind,
    // Without the converter the value arrives as JsonElement and every template reading it
    // breaks, exactly as it would crossing any other Dapr boundary.
    [property: JsonConverter(typeof(ObjectDictionaryJsonConverter))]
    object? Value,
    string? ErrorMessage = null);
