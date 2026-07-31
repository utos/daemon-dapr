using Execution.Domain.Aggregates;

namespace Execution.Features.Models;

/// <summary>
/// Summary information for workflow execution (POCO/DTO version)
/// Used for list views with key execution information
/// </summary>
public class ExecutionSummary
{
    public Guid Id { get; set; }
    public ExecutionStatus Status { get; set; }
    public string WorkflowName { get; set; } = null!;
    public string WorkflowVersion { get; set; } = null!;
    public string? WorkflowNamespace { get; set; }
    public string? WorkflowRegistry { get; set; }
    public string? WorkflowDigest { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
