using Common.SeedWork;
using Common.Workflow;
using Execution.Domain.Aggregates;

namespace Execution.Application.Models;

/// <summary>
/// Complete detailed information for workflow execution (POCO/DTO version)
/// Includes full workflow definition and execution history
/// </summary>
public class ExecutionDetails
{
    public ExecutionId Id { get; set; }
    public ExecutionStatus Status { get; set; }
    public WorkflowIdentity Workflow { get; set; } = null!;
    public string? WorkflowDigest { get; set; }
    public WorkflowBundle Bundle { get; set; } = null!;
    public Dictionary<string, object> Input { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }

    /// <summary>When the run was cancelled. Null unless Status is Cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Why it was cancelled, when a reason was given.</summary>
    public string? CancellationReason { get; set; }
    public string? ErrorMessage { get; set; }
}
