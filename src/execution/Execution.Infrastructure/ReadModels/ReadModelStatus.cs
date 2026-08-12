namespace Execution.Infrastructure.ReadModels;

/// <summary>
/// String constants for workflow execution read model status values.
/// These are the values stored in the database Status column.
/// </summary>
public static class ReadModelStatus
{
    public const string Unspecified = "UNSPECIFIED";
    public const string Scheduled = "SCHEDULED";
    public const string Active = "ACTIVE";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}
