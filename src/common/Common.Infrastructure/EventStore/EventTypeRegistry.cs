using System.Reflection;
using Common.SeedWork;

namespace Common.Infrastructure.EventStore;

/// <summary>
/// The two-way map between an event's stored name and its CLR type — the one place either
/// direction is decided.
///
/// Built by scanning an assembly rather than from a hand-written list, because a list is
/// something you can forget to add to, and forgetting is precisely the silent failure this type
/// exists to remove. An <see cref="IDomainEvent"/> without an <see cref="EventTypeAttribute"/>
/// fails construction, so it fails at startup rather than on the first read of a stream nobody
/// looked at for a week.
/// </summary>
public sealed class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = [];
    private readonly Dictionary<Type, EventTypeAttribute> _byType = [];

    public EventTypeRegistry(params Assembly[] assemblies)
        : this(assemblies.SelectMany(assembly => assembly.GetTypes()))
    {
    }

    /// <summary>
    /// A registry over named types rather than whole assemblies. For tests that need a registry
    /// holding exactly the events they declared; production scans, so that nothing is left out.
    /// </summary>
    public static EventTypeRegistry For(params Type[] eventTypes) => new(eventTypes.AsEnumerable());

    private EventTypeRegistry(IEnumerable<Type> candidates)
    {
        var events = candidates.Where(type =>
            typeof(IDomainEvent).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });

        foreach (var type in events)
        {
            var attribute = type.GetCustomAttribute<EventTypeAttribute>()
                            ?? throw new InvalidOperationException(
                                $"{type.FullName} implements {nameof(IDomainEvent)} but carries no " +
                                $"[{nameof(EventTypeAttribute)}]. Every persisted event declares the name it " +
                                "is stored under; without one it would be written under a name no reader " +
                                "resolves.");

            if (_byName.TryGetValue(attribute.Name, out var existing))
                throw new InvalidOperationException(
                    $"Event name '{attribute.Name}' is declared by both {existing.FullName} and " +
                    $"{type.FullName}. A name resolves to one type or a stream cannot be read back.");

            _byName[attribute.Name] = type;
            _byType[type] = attribute;
        }
    }

    /// <summary>Every registered name, for tests that assert the set rather than one member of it.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    public string NameOf(Type eventType) => Attribute(eventType).Name;

    public int CurrentVersionOf(Type eventType) => Attribute(eventType).Version;

    public bool TryResolve(string name, out Type eventType) => _byName.TryGetValue(name, out eventType!);

    private EventTypeAttribute Attribute(Type eventType) =>
        _byType.TryGetValue(eventType, out var attribute)
            ? attribute
            : throw new InvalidOperationException(
                $"{eventType.FullName} is not a registered event type. Its assembly is not one of the " +
                "assemblies passed to AddEventStoreSerialization.");
}
