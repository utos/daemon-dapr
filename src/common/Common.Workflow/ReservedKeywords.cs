namespace Common.Workflow;

/// <summary>
/// Reserved keywords that cannot be used as activity names.
/// These are special identifiers used by the workflow engine for control flow.
/// </summary>
public static class ReservedKeywords
{
    /// <summary>
    /// Reserved keyword indicating workflow should complete successfully.
    /// Used as a transition target to explicitly end the workflow.
    /// </summary>
    public const string End = "end";

    /// <summary>
    /// Reserved keyword indicating workflow should fail.
    /// Used as a transition target to explicitly fail the workflow.
    /// </summary>
    public const string Error = "error";

    /// <summary>
    /// All reserved keywords that cannot be used as activity names.
    /// </summary>
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        End,
        Error
    };

    /// <summary>
    /// Checks if a given name is a reserved keyword.
    /// </summary>
    /// <param name="name">The name to check</param>
    /// <returns>True if the name is reserved, false otherwise</returns>
    public static bool IsReserved(string name)
    {
        return All.Contains(name);
    }
}
