namespace Common.SeedWork;

public record StreamTerminatedEvent(DateTime DateOccurred) : IDomainEvent;
