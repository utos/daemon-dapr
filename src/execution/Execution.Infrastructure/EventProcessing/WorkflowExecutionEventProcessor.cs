using Common.Constants;
using Common.Infrastructure.EventHandling;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Extensions;
using Common.Infrastructure.Projections;
using Grpc.Core;
using KurrentDB.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Execution.Infrastructure.EventProcessing;

public class WorkflowExecutionEventProcessor(
    KurrentDBPersistentSubscriptionsClient persistentSubscriptionsClient,
    IServiceProvider serviceProvider,
    IEventSerializer eventSerializer,
    ILogger<WorkflowExecutionEventProcessor> logger)
    : BackgroundService
{
    private const string GroupName = SubscriptionName.WorkflowEventProcessor;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting WorkflowExecution event subscription service");

        await EnsureSubscriptionExists(cancellationToken);

        var subscription =
            persistentSubscriptionsClient.SubscribeToAll(GroupName, cancellationToken: cancellationToken);

        await foreach (var message in subscription.Messages.WithCancellation(cancellationToken))
            switch (message)
            {
                case PersistentSubscriptionMessage.Event(var resolvedEvent, _):
                    try
                    {
                        await ProcessEvent(resolvedEvent, cancellationToken);
                        await subscription.Ack(resolvedEvent);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing event {EventType}", resolvedEvent.Event.EventType);
                        await subscription.Nack(PersistentSubscriptionNakEventAction.Park, ex.Message, resolvedEvent);
                    }

                    break;

                case PersistentSubscriptionMessage.SubscriptionConfirmation confirmation:
                    logger.LogInformation(
                        "Persistent subscription confirmed with ID {SubscriptionId}",
                        confirmation.SubscriptionId);
                    break;
            }
    }

    private async Task EnsureSubscriptionExists(CancellationToken cancellationToken)
    {
        try
        {
            await persistentSubscriptionsClient.CreateToAllAsync(
                GroupName,
                EventTypeFilter.Prefix("Execution.Domain.Events"),
                new PersistentSubscriptionSettings(),
                cancellationToken: cancellationToken);

            logger.LogInformation("Created persistent subscription group '{GroupName}'", GroupName);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation("Persistent subscription group '{GroupName}' already exists", GroupName);
        }
    }

    private async Task ProcessEvent(ResolvedEvent resolvedEvent, CancellationToken cancellationToken)
    {
        var eventData = resolvedEvent.GetEventData();
        var eventType = resolvedEvent.GetEventType();
        var metadata = resolvedEvent.GetMetadata();

        var domainEvent = eventSerializer.DeserializeEvent(eventType, eventData, metadata);

        if (domainEvent == null)
        {
            logger.LogWarning("Failed to deserialize event {EventType}", eventType);
            return;
        }

        var position = resolvedEvent.GetEventNumber();

        using var scope = serviceProvider.CreateScope();

        // Projections: idempotent read model updates (safe to replay)
        var projectionEngine = scope.ServiceProvider.GetRequiredService<IProjectionEngine>();
        await projectionEngine.ProcessEventAsync(domainEvent, position, cancellationToken);

        // Event handlers: side-effect-producing reactions (not safe to replay)
        var eventHandlers = scope.ServiceProvider.GetRequiredService<IEnumerable<IEventHandler>>();
        var domainEventType = domainEvent.GetType();
        foreach (var handler in eventHandlers)
        {
            if (!handler.CanHandle(domainEventType))
                continue;

            try
            {
                await handler.HandleAsync(domainEvent, position, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Event handler {HandlerType} failed to handle event {EventType} at position {Position}",
                    handler.GetType().Name, domainEventType.Name, position);
                throw;
            }
        }
    }
}
