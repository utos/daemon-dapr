using System.Text.Json.Nodes;

namespace Common.Infrastructure.EventStore;

/// <summary>
/// One step of a stored event's evolution: takes the JSON written at <see cref="FromVersion"/>
/// and returns the shape of <c>FromVersion + 1</c>.
///
/// One step, not one jump to current, so a type that has moved through three versions needs three
/// small transformations rather than one that has to know every path through the history.
/// </summary>
public interface IEventUpcaster
{
    /// <summary>The stored name, as declared by the event's <c>[EventType]</c>.</summary>
    string EventTypeName { get; }

    int FromVersion { get; }

    JsonObject Upcast(JsonObject payload);
}
