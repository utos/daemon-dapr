namespace Common.SeedWork;

/// <summary>
/// The name an event is stored under, and the schema version it is currently written at.
///
/// Declared here rather than derived from the CLR type, because the type's name, namespace and
/// assembly are all things a refactor moves and none of them should reach the wire. The stored
/// name is what a reader looks the type back up by, so a rename that changed it would not fail —
/// it would make every event written before the rename unreadable.
///
/// <see cref="Version"/> is written into the event's metadata and is what the upcaster chain
/// counts up from. Bump it only when the JSON shape changes in a way the previous shape cannot be
/// deserialized into, and register an <c>IEventUpcaster</c> for the step; an added optional field
/// needs neither.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EventTypeAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public int Version { get; init; } = 1;
}
