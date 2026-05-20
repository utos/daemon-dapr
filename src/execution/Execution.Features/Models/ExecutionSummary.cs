using Utos.Daemon.V1;

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
    public DateTime CreatedAt { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
