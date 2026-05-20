namespace Common.SeedWork;

public interface IDomainEvent
{
    DateTime DateOccurred { get; }
}
