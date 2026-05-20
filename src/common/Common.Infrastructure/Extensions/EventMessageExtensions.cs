using KurrentDB.Client;

namespace Common.Infrastructure.Extensions;

public static class EventMessageExtensions
{
    public static byte[] GetEventData(this StreamMessage.Event eventMessage)
    {
        return eventMessage.ResolvedEvent.Event.Data.ToArray();
    }

    public static string GetEventType(this StreamMessage.Event eventMessage)
    {
        return eventMessage.ResolvedEvent.Event.EventType;
    }

    public static byte[] GetMetadata(this StreamMessage.Event eventMessage)
    {
        return eventMessage.ResolvedEvent.Event.Metadata.ToArray();
    }

    public static long GetEventNumber(this StreamMessage.Event eventMessage)
    {
        return eventMessage.ResolvedEvent.OriginalEventNumber.ToInt64();
    }

    public static ulong GetGlobalPosition(this StreamMessage.Event eventMessage)
    {
        return eventMessage.ResolvedEvent.OriginalPosition?.CommitPosition ?? 0;
    }

    // ResolvedEvent overloads (for persistent subscriptions)

    public static byte[] GetEventData(this ResolvedEvent resolvedEvent)
    {
        return resolvedEvent.Event.Data.ToArray();
    }

    public static string GetEventType(this ResolvedEvent resolvedEvent)
    {
        return resolvedEvent.Event.EventType;
    }

    public static byte[] GetMetadata(this ResolvedEvent resolvedEvent)
    {
        return resolvedEvent.Event.Metadata.ToArray();
    }

    public static long GetEventNumber(this ResolvedEvent resolvedEvent)
    {
        return resolvedEvent.OriginalEventNumber.ToInt64();
    }

    public static ulong GetGlobalPosition(this ResolvedEvent resolvedEvent)
    {
        return resolvedEvent.OriginalPosition?.CommitPosition ?? 0;
    }
}
