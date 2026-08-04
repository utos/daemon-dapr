namespace Common.Infrastructure.EventStore;

/// <summary>
/// The upcasters, indexed by the step they perform. Two upcasters claiming the same step is a
/// contradiction about what a version means, so it fails at startup rather than letting whichever
/// one was registered last decide.
/// </summary>
public sealed class EventUpcasterRegistry
{
    private readonly Dictionary<(string Name, int FromVersion), IEventUpcaster> _upcasters = [];

    public EventUpcasterRegistry(IEnumerable<IEventUpcaster> upcasters)
    {
        foreach (var upcaster in upcasters)
        {
            var key = (upcaster.EventTypeName, upcaster.FromVersion);

            if (_upcasters.TryGetValue(key, out var existing))
                throw new InvalidOperationException(
                    $"Two upcasters claim {key.EventTypeName} v{key.FromVersion} -> " +
                    $"v{key.FromVersion + 1}: {existing.GetType().FullName} and " +
                    $"{upcaster.GetType().FullName}.");

            _upcasters[key] = upcaster;
        }
    }

    public bool TryGet(string eventTypeName, int fromVersion, out IEventUpcaster upcaster) =>
        _upcasters.TryGetValue((eventTypeName, fromVersion), out upcaster!);
}
