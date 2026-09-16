namespace Common.SeedWork;

/// <summary>
/// Generic in its identifier so the kernel stays free of any one aggregate's concepts: an
/// aggregate brings its own id type rather than every aggregate sharing a bare <see cref="Guid"/>.
/// </summary>
public abstract class AggregateRoot<TId> where TId : notnull
{
    /// <summary>Version of an aggregate whose stream does not exist yet.</summary>
    public const long NoStream = -1;

    public TId Id { get; protected set; } = default!;

    public DateTime CreatedAt { get; protected set; }

    /// <summary>
    /// The stream revision this aggregate was loaded at, excluding anything not yet committed.
    /// Used as the expected version when appending, so a writer working from stale state is
    /// rejected instead of silently interleaving.
    /// </summary>
    public long Version { get; private set; } = NoStream;

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> GetDomainEvents()
    {
        return _domainEvents.AsReadOnly();
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    /// <param name="streamRevision">
    /// The revision of the last event in the stream. Supplied by the store rather than counted
    /// from <paramref name="events"/>: the revision is the store's own fact, and it is what the
    /// next append is checked against.
    /// </param>
    public void LoadFromHistory(IEnumerable<IDomainEvent> events, long streamRevision)
    {
        foreach (var @event in events) When(@event);

        Version = streamRevision;
    }

    /// <summary>
    /// Advances the version past events that have just been appended. Counting up from
    /// <see cref="NoStream"/> works out: appending n events to an empty stream leaves the
    /// stream at revision n-1.
    /// </summary>
    public void MarkCommitted(int appendedCount)
    {
        Version += appendedCount;
    }

    protected void ApplyChange(IDomainEvent @event)
    {
        When(@event);
        _domainEvents.Add(@event);
    }

    protected abstract void When(IDomainEvent @event);
}
