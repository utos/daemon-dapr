using Common.ExecutionLifecycle;
using Common.Infrastructure.EventHandling;
using Common.SeedWork;
using Execution.Domain.Events;

namespace Execution.Infrastructure.EventHandling;

/// <summary>
/// Translates the two terminal domain events into the fact other contexts are allowed to know.
/// This is the only place that reads them on anyone else's behalf: a listener learns that a run
/// ended without ever seeing the aggregate's events, which is what lets its module stop
/// referencing this one.
/// </summary>
public class ExecutionLifecycleNotifier(IEnumerable<IExecutionLifecycleListener> listeners) : IEventHandler
{
    public async Task HandleAsync(IDomainEvent @event, long position, CancellationToken cancellationToken = default)
    {
        // Sequential and awaited: a listener that throws has to reach the event processor, which
        // parks the event rather than acknowledging a side effect that never happened.
        switch (@event)
        {
            case WorkflowExecutionCompletedEvent e:
                foreach (var listener in listeners)
                    await listener.OnCompletedAsync(e.ExecutionId, e.DateOccurred, cancellationToken);
                break;

            case WorkflowExecutionFailedEvent e:
                foreach (var listener in listeners)
                    await listener.OnFailedAsync(e.ExecutionId, e.DateOccurred, e.Reason, cancellationToken);
                break;
        }
    }

    public bool CanHandle(Type eventType)
        => eventType == typeof(WorkflowExecutionCompletedEvent)
        || eventType == typeof(WorkflowExecutionFailedEvent);
}
