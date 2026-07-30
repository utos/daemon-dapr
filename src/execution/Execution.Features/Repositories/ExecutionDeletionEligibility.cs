namespace Execution.Features.Repositories;

/// <summary>
/// Whether an execution may be deleted, derived from its read-model status.
/// Keeps the application layer free of Infrastructure-only status string constants.
/// </summary>
public enum ExecutionDeletionEligibility
{
    /// <summary>No execution with the given id exists.</summary>
    NotFound,

    /// <summary>The execution exists but is not terminal (still SCHEDULED/ACTIVE/UNSPECIFIED).</summary>
    NonTerminal,

    /// <summary>The execution exists and is terminal (COMPLETED or FAILED) — safe to delete.</summary>
    Terminal
}
