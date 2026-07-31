using Common.Workflow;
using Execution.Domain.Aggregates;

namespace Execution.Features.Models;

/// <summary>
/// Complete detailed information for workflow execution (POCO/DTO version)
/// Includes full workflow definition and execution history
/// </summary>
public class ExecutionDetails
{
    public Guid Id { get; set; }
    public ExecutionStatus Status { get; set; }
    public string WorkflowName { get; set; } = null!;
    public string WorkflowVersion { get; set; } = null!;
    public string? WorkflowNamespace { get; set; }
    public string? WorkflowRegistry { get; set; }
    public string? WorkflowDigest { get; set; }
    public WorkflowBundle Bundle { get; set; } = null!;
    public Dictionary<string, object> Input { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
