namespace Common.SeedWork;

public abstract class AggregateRoot
{
    public Guid Id { get; protected set; }

    public DateTime CreatedAt { get; protected set; }

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> GetDomainEvents()
    {
        return _domainEvents.AsReadOnly();
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    public void LoadFromHistory(IEnumerable<IDomainEvent> events)
    {
        foreach (var @event in events) When(@event);
    }

    protected void ApplyChange(IDomainEvent @event)
    {
        When(@event);
        _domainEvents.Add(@event);
    }

    protected abstract void When(IDomainEvent @event);
}
