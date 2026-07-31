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
                store.CompleteWith(e.ExecutionId, e.DateOccurred, ExecutionOutcome.Completed, null);
                break;
            case WorkflowExecutionFailedEvent e:
                store.CompleteWith(e.ExecutionId, e.DateOccurred, ExecutionOutcome.Failed, e.Reason);
                break;
        }
        return Task.CompletedTask;
    }

    public bool CanHandle(Type eventType)
        => eventType == typeof(WorkflowExecutionCompletedEvent)
        || eventType == typeof(WorkflowExecutionFailedEvent);
}
