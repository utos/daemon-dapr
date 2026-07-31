namespace Common.SeedWork;

public static class StreamNames
{
    /// <summary>
    /// The stream holding one aggregate's events, named "{aggregate}-{id}". The single place
    /// this format is written: a second copy elsewhere would go unnoticed until it drifted and
    /// pointed readers at a stream nothing writes.
    /// </summary>
    public static string For<T>(object aggregateId) => For(typeof(T).Name, aggregateId);

    public static string For(string aggregateType, object aggregateId) =>
        $"{aggregateType.ToLowerInvariant()}-{aggregateId}";

    /// <summary>
    /// An execution's stream, for callers outside the execution slice that hold only an id and
    /// cannot name the aggregate type.
    /// </summary>
    public static string Execution(ExecutionId id) => For("WorkflowExecution", id);
}
