using Common.SeedWork;

namespace Common.Infrastructure.Projections;

public interface IProjectionEngine
{
    Task ProcessEventAsync(IDomainEvent @event, long position, CancellationToken ct = default);
}
