using Common.SeedWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Projections;

public abstract class DbProjection(DbContext dbContext, ILogger logger) : IProjection
{
    public async Task HandleAsync(IDomainEvent @event, long position, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await ProjectEventAsync(@event, position, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error projecting event {EventType}", @event.GetType().Name);
            throw;
        }
    }

    public abstract bool CanHandle(Type eventType);

    /// <summary>
    /// Projects the event to update read models.
    /// </summary>
    protected abstract Task ProjectEventAsync(IDomainEvent @event, long position, CancellationToken cancellationToken);
}
