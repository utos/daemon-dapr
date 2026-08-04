using Common.SeedWork;

namespace Common.Infrastructure.EventStore;

public interface IEventSerializer
{
    SerializedEvent SerializeEvent(IDomainEvent domainEvent);

    /// <exception cref="EventDeserializationException">
    /// The event cannot be read. It throws rather than returning null — see
    /// <see cref="EventDeserializationException"/> for what returning null used to cost.
    /// </exception>
    IDomainEvent DeserializeEvent(SerializedEvent serializedEvent);

    /// <inheritdoc cref="DeserializeEvent(SerializedEvent)"/>
    IDomainEvent DeserializeEvent(string eventType, byte[] eventData, byte[] metadata);
}
