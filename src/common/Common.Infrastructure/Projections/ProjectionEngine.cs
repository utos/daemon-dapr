using Common.SeedWork;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Projections;

public class ProjectionEngine(IEnumerable<IProjection> projections, ILogger<ProjectionEngine> logger)
    : IProjectionEngine
{
    public async Task ProcessEventAsync(IDomainEvent @event, long position, CancellationToken ct = default)
    {
        var eventType = @event.GetType();

        foreach (var projection in projections)
        {
            if (!projection.CanHandle(eventType))
                continue;

            try
            {
                await projection.HandleAsync(@event, position, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Projection {ProjectionType} failed to handle event {EventType} at position {Position}",
                    projection.GetType().Name, eventType.Name, position);
                throw; // Re-throw to fail fast - projections should not silently fail
            }
        }
    }
}
