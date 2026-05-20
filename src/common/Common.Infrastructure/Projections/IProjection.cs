using Common.SeedWork;

namespace Common.Infrastructure.Projections;

/// <summary>
/// Represents a projection that processes domain events to update read models.
/// </summary>
public interface IProjection
{
    /// <summary>
    /// Handles a domain event by updating the appropriate read models.
    /// </summary>
    /// <param name="event">The domain event to process.</param>
    /// <param name="position">The global position of the event in the event store.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleAsync(IDomainEvent @event, long position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether this projection can handle the specified event type.
    /// </summary>
    /// <param name="eventType">The type of event to check.</param>
    /// <returns>True if the projection can handle the event type; otherwise, false.</returns>
    bool CanHandle(Type eventType);
}
