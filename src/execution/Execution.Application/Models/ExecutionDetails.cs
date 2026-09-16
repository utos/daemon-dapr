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

    /// <summary>The failure's authored code, or null when the daemon raised the failure.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The failure's structured details, read from the output stream's terminal entry the way
    /// <see cref="Result"/> is. Null when the run did not fail, or failed without any.
    /// </summary>
    public ValueMap? ErrorDetails { get; set; }

    /// <summary>
    /// What the run returned — the <c>result</c> terminal entry of its output stream.
    /// <para>
    /// Null means there is no value to report: the run has not finished; it failed or was
    /// cancelled and reports <see cref="ErrorMessage"/> or <see cref="CancellationReason"/>
    /// instead; or it completed without a value — a bare <c>return</c>, or its rules running out
    /// — which spec 0.0.16 reports as an unset result, the status being what says it completed.
    /// </para>
    /// </summary>
    public ValueMap? Result { get; set; }
}
