using System.Reflection;
using Common.Infrastructure.EventStore;
using Common.SeedWork;
using Execution.Domain.Events;
using Execution.Domain.Output;

namespace UnitTests.Persistence;

/// <summary>
/// The name an event is stored under, pinned as literal text — the half of the persistence
/// contract <see cref="EventJsonShapeTests"/> does not cover.
///
/// That test pins each event's JSON body, which is why five refactors in a row moved types around
/// without breaking a stream. None of them touched an event's own namespace or assembly, and
/// nothing would have caught it if they had: the stored name used to be the type's
/// <c>AssemblyQualifiedName</c>, so a move made every event written before it unreadable, and
/// unreadable meant silently dropped. The names below are now declared, and asserting them here
/// as strings is what turns a rename into a failing build instead of a lost stream.
/// </summary>
public class EventTypeRegistryTests
{
    private static readonly Assembly ExecutionEvents = typeof(WorkflowExecutionCreatedEvent).Assembly;

    private static readonly EventTypeRegistry Registry = new(ExecutionEvents);

    /// <summary>The prefix the read model's persistent subscription filters on.</summary>
    private const string LifecyclePrefix = "utos.execution.";

    [Theory]
    [InlineData(typeof(WorkflowExecutionCreatedEvent), "utos.execution.created")]
    [InlineData(typeof(WorkflowExecutionScheduledEvent), "utos.execution.scheduled")]
    [InlineData(typeof(WorkflowExecutionActiveEvent), "utos.execution.active")]
    [InlineData(typeof(WorkflowExecutionCompletedEvent), "utos.execution.completed")]
    [InlineData(typeof(WorkflowExecutionFailedEvent), "utos.execution.failed")]
    [InlineData(typeof(WorkflowExecutionCancelledEvent), "utos.execution.cancelled")]
    [InlineData(typeof(ExecutionValueEmitted), "utos.output.value")]
    [InlineData(typeof(ExecutionResultProduced), "utos.output.result")]
    [InlineData(typeof(ExecutionErrorProduced), "utos.output.error")]
    public void An_event_is_stored_under_the_name_it_declares(Type eventType, string storedName)
    {
        Assert.Equal(storedName, Registry.NameOf(eventType));

        Assert.True(Registry.TryResolve(storedName, out var resolved));
        Assert.Equal(eventType, resolved);
    }

    /// <summary>
    /// Guards the theory above against passing while saying nothing about an event added later:
    /// a tenth event is either named here or it fails this.
    /// </summary>
    [Fact]
    public void Every_event_in_the_module_is_named_above()
    {
        string[] named =
        [
            "utos.execution.active",
            "utos.execution.cancelled",
            "utos.execution.completed",
            "utos.execution.created",
            "utos.execution.failed",
            "utos.execution.scheduled",
            "utos.output.error",
            "utos.output.result",
            "utos.output.value"
        ];

        Assert.Equal(named, Registry.Names.Order());
    }

    [Theory]
    [InlineData(typeof(WorkflowExecutionCreatedEvent))]
    [InlineData(typeof(WorkflowExecutionScheduledEvent))]
    [InlineData(typeof(WorkflowExecutionActiveEvent))]
    [InlineData(typeof(WorkflowExecutionCompletedEvent))]
    [InlineData(typeof(WorkflowExecutionFailedEvent))]
    [InlineData(typeof(WorkflowExecutionCancelledEvent))]
    public void A_lifecycle_event_carries_the_prefix_the_subscription_filters_on(Type eventType)
    {
        Assert.StartsWith(LifecyclePrefix, Registry.NameOf(eventType));
    }

    /// <summary>
    /// The other half, and the one that is easy to get wrong. The read model's subscription
    /// matches <see cref="LifecyclePrefix"/> server-side, so an output entry named under it would
    /// be handed to every projection and every event handler on each emitted value — work nobody
    /// asked for, on the hot path of a workflow that emits in a loop.
    ///
    /// It would not fail, either: the projections would find no case for the entry and do
    /// nothing. This rule used to be enforced by which folder the file sat in, so a routine move
    /// would have silently changed what the daemon does. It is a name now, and this is what holds
    /// it.
    /// </summary>
    [Theory]
    [InlineData(typeof(ExecutionValueEmitted))]
    [InlineData(typeof(ExecutionResultProduced))]
    [InlineData(typeof(ExecutionErrorProduced))]
    public void An_output_entry_stays_outside_that_prefix(Type eventType)
    {
        Assert.DoesNotContain(LifecyclePrefix, Registry.NameOf(eventType));
    }

    [Fact]
    public void An_event_without_a_declared_name_is_rejected_at_construction()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new EventTypeRegistry(typeof(UnnamedEvent).Assembly));

        Assert.Contains(nameof(EventTypeAttribute), exception.Message);
    }

    [Fact]
    public void A_type_from_an_unregistered_assembly_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Registry.NameOf(typeof(NamedTestEvent)));
    }

    /// <summary>Declared in this assembly, which is what makes the rejection test above find it.</summary>
    private record UnnamedEvent(DateTime DateOccurred) : IDomainEvent;

    [EventType("utos.test.named")]
    private record NamedTestEvent(DateTime DateOccurred) : IDomainEvent;
}
