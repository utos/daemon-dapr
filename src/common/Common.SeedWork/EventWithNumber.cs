namespace Common.SeedWork;

public record EventWithNumber(
    IDomainEvent Event,
    long EventNumber
);
