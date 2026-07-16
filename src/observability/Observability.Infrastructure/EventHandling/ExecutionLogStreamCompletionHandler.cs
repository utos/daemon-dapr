using Common.Infrastructure.EventHandling;
using Common.SeedWork;
using Execution.Domain.Events;
using Observability.Domain;

namespace Observability.Infrastructure.EventHandling;

public class ExecutionLogStreamCompletionHandler(IExecutionLogStore store) : IEventHandler
{
    public Task HandleAsync(IDomainEvent @event, long position, CancellationToken cancellationToken = default)
    {
        switch (@event)
        {
            case WorkflowExecutionCompletedEvent e:
                store.Complete(e.ExecutionId);
                break;
            case WorkflowExecutionFailedEvent e:
                store.Complete(e.ExecutionId);
                break;
        }
        return Task.CompletedTask;
    }

    public bool CanHandle(Type eventType)
        => eventType == typeof(WorkflowExecutionCompletedEvent)
        || eventType == typeof(WorkflowExecutionFailedEvent);
}
