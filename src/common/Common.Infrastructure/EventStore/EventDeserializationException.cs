namespace Common.Infrastructure.EventStore;

/// <summary>
/// An event in a stream could not be turned back into a domain event.
///
/// It throws rather than returning null on purpose. The two readers both used to treat an
/// unreadable event as one to skip: the repository dropped it while still counting its stream
/// revision, so the aggregate rehydrated with a hole at the correct revision and the next append
/// cemented it; the event processor returned and the caller acked, so the read model quietly
/// diverged. Neither surfaced anywhere but a log line. A load that throws fails the command that
/// asked for it, and a subscription that throws parks the event — both are recoverable, and
/// neither is silent.
/// </summary>
public sealed class EventDeserializationException(string message, Exception? inner = null)
    : Exception(message, inner);
