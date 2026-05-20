using Common.SeedWork;

namespace Execution.Infrastructure.ReadModels;

/// <summary>
/// Read model for workflow executions optimized for querying.
/// Denormalized view projected from workflow execution events.
/// </summary>
public class WorkflowExecutionReadModel : Entity
{
    /// <summary>
    /// Parent execution ID for sub-workflow executions. Null for top-level executions.
    /// </summary>
    public Guid? ParentExecutionId { get; set; }

    /// <summary>
    /// Current execution status: INITIAL, SCHEDULED, ACTIVE, COMPLETED, FAILED
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// Workflow name from metadata
    /// </summary>
    public string WorkflowName { get; set; } = null!;

    /// <summary>
    /// Workflow version from metadata (semantic version)
    /// </summary>
    public string WorkflowVersion { get; set; } = null!;

    /// <summary>
    /// When the execution was scheduled for execution
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// When the workflow execution started (became ACTIVE)
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the workflow execution completed successfully
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When the workflow execution failed
    /// </summary>
    public DateTime? FailedAt { get; set; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Full workflow definition as JSON (for GetExecution details)
    /// Stored as JSON to avoid complex relational mapping
    /// </summary>
    public string? WorkflowDefinitionJson { get; set; }

    /// <summary>
    /// Execution payload as JSON (input data for workflow)
    /// </summary>
    public string? InputJson { get; set; }

    // Audit fields inherited from Entity:
    // - CreatedAt (when first event was received)
    // - LastModifiedAt (when last event was received)
    // - Version (for optimistic concurrency)
}
