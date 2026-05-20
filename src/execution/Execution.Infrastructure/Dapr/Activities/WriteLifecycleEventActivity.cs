using Common.SeedWork;
using Dapr.Workflow;
using Execution.Domain.Events;
using Execution.Domain.Services;

namespace Execution.Infrastructure.Dapr.Activities;

public sealed record WriteLifecycleEventInput(
    Guid ExecutionId,
    string EventType,
    string? FailureReason);

public class WriteLifecycleEventActivity(
    IEventStoreRepository eventStore,
    IExecutionContextStore contextStore)
    : WorkflowActivity<WriteLifecycleEventInput, object>
{
    public override async Task<object> RunAsync(
        WorkflowActivityContext context, WriteLifecycleEventInput input)
    {
        var streamName = StreamNames.Execution(input.ExecutionId);

        IDomainEvent @event = input.EventType switch
        {
            "Active" => new WorkflowExecutionActiveEvent(input.ExecutionId, DateTime.UtcNow),
            "Completed" => new WorkflowExecutionCompletedEvent(input.ExecutionId, DateTime.UtcNow),
            "Failed" => new WorkflowExecutionFailedEvent(
                input.ExecutionId, input.FailureReason!, DateTime.UtcNow),
            _ => throw new ArgumentException($"Unknown event type: {input.EventType}")
        };

        await eventStore.AppendToStreamAsync(streamName, @event);

        if (input.EventType is "Completed" or "Failed")
            await contextStore.DeleteAllAsync(input.ExecutionId);

        return new object();
    }
}
