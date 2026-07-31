using Observability.Domain;

namespace Observability.Application.Models;

/// <summary>
/// One frame of an execution's watch stream, already flattened into the shape a watcher filters
/// on. Log lines and terminal frames are deliberately the same shape: a filter applies to both,
/// so a COMPLETED frame (Info, source "system") is excluded by an error-level filter, while a
/// FAILED frame (Error) still passes one.
/// </summary>
public sealed record ExecutionFrame(
    DateTime Timestamp,
    ExecutionFrameLevel Level,
    string Source,
    string Category,
    string Message,
    ExecutionOutcome? Terminal = null,
    string? FailureReason = null);
