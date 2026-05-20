namespace Common.SeedWork;

public record SerializedEvent(
    string EventType,
    byte[] Data,
    byte[] Metadata
);
