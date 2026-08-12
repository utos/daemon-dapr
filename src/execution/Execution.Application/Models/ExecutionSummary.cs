using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Aggregates;

namespace Execution.Application.Models;

/// <summary>
/// Summary information for workflow execution (POCO/DTO version)
/// Used for list views with key execution information
/// </summary>
public class ExecutionSummary
{
    public ExecutionId Id { get; set; }
    public ExecutionStatus Status { get; set; }
    public WorkflowIdentity Workflow { get; set; } = null!;
    public string? WorkflowDigest { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Why the run was cancelled. Null unless Status is Cancelled — and null then too
    /// when no reason was given. Separate from ErrorMessage so a deliberate stop never reads as
    /// a failure.</summary>
    public string? CancellationReason { get; set; }
}
