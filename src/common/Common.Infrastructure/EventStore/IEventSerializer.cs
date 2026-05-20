using Common.SeedWork;

namespace Common.Infrastructure.EventStore;

public interface IEventSerializer
{
    SerializedEvent SerializeEvent(IDomainEvent domainEvent);
    IDomainEvent? DeserializeEvent(SerializedEvent serializedEvent);
    IDomainEvent? DeserializeEvent(string eventType, byte[] eventData, byte[] metadata);
}
