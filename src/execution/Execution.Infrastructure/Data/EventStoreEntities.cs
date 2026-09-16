namespace Execution.Infrastructure.Data;

/// <summary>
/// The event table as EF Core sees it — for the schema and its migration only. The stores
/// never load these; see <c>EventTables</c>.
/// </summary>
public sealed class StoredEvent
{
    /// <summary>Global order, the <c>$all</c> position; an identity, and commit order under the append lock.</summary>
    public long Position { get; set; }

    public string Stream { get; set; } = string.Empty;

    /// <summary>0-based within the stream: the revision an aggregate is loaded at and appends are checked against.</summary>
    public long Revision { get; set; }

    /// <summary>The <c>[EventType]</c> name, e.g. <c>utos.execution.failed</c>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The <c>[EventType]</c> version the body was written at.</summary>
    public int Version { get; set; }

    /// <summary>The serializer's body, as it produced it.</summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>The serializer's metadata, as it produced it.</summary>
    public string Metadata { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }
}

/// <summary>Where a subscriber group has read to. Durable, so a restart resumes rather than replays.</summary>
public sealed class SubscriptionCheckpoint
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The last global position processed, or 0 for none.</summary>
    public long Position { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An event a subscriber could not process. Recorded and moved past, the way a KurrentDB
/// persistent subscription parks — but in a table an operator can query.
/// </summary>
public sealed class ParkedEvent
{
    public long Position { get; set; }

    public string Subscription { get; set; } = string.Empty;

    public string Stream { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public DateTime ParkedAt { get; set; }
}
