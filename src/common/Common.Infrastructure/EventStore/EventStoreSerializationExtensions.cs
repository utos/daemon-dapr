using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Infrastructure.EventStore;

public static class EventStoreSerializationExtensions
{
    /// <summary>
    /// Registers the event wire format over the events declared in <paramref name="eventAssemblies"/>.
    ///
    /// Takes assemblies rather than a list of types so that this project stays ignorant of the
    /// modules — a module hands over its own assembly, and any event in it that forgot to declare
    /// a name fails here, at startup.
    /// </summary>
    public static IServiceCollection AddEventStoreSerialization(
        this IServiceCollection services,
        params Assembly[] eventAssemblies)
    {
        services.AddSingleton(new EventTypeRegistry(eventAssemblies));
        services.AddSingleton(provider =>
            new EventUpcasterRegistry(provider.GetServices<IEventUpcaster>()));
        services.AddSingleton<IEventSerializer, EventSerializer>();

        return services;
    }
}
