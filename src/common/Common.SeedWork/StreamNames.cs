namespace Common.SeedWork;

public static class StreamNames
{
    public static string Execution(Guid id)
    {
        return $"workflowexecution-{id}";
    }
}
