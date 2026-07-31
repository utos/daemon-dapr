namespace Common.SeedWork;

/// <summary>
/// An append was rejected because the stream had moved on since the aggregate was loaded —
/// someone else wrote first. The caller's decision was made against stale state, so the fix is
/// to reload and decide again, not to retry the same append.
///
/// Store-neutral on purpose: the concrete event store's version-conflict type stays behind
/// <see cref="IEventStoreRepository"/>.
/// </summary>
public class ConcurrencyException : Exception
{
    public ConcurrencyException(string message) : base(message)
    {
    }

    public ConcurrencyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
