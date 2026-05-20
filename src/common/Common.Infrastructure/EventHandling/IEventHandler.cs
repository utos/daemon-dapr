using Common.SeedWork;

namespace Common.Infrastructure.EventHandling;

/// <summary>
/// Represents a handler that reacts to domain events by triggering side effects
/// (e.g., scheduling workflows, sending notifications, issuing commands).
/// Unlike <see cref="Projections.IProjection"/>, event handlers are NOT idempotent
/// and should NOT be replayed when rebuilding read models.
/// </summary>
public interface IEventHandler
{
    /// <summary>
    /// Handles a domain event by executing side effects.
    /// </summary>
    /// <param name="event">The domain event to process.</param>
    /// <param name="position">The global position of the event in the event store.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task HandleAsync(IDomainEvent @event, long position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether this handler can handle the specified event type.
    /// </summary>
    /// <param name="eventType">The type of event to check.</param>
    /// <returns>True if the handler can handle the event type; otherwise, false.</returns>
    bool CanHandle(Type eventType);
}
