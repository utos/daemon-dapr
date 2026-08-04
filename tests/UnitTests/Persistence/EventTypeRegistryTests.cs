using System.Reflection;
using Common.Infrastructure.EventStore;
using Common.SeedWork;
using Execution.Domain.Events;

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

    [Theory]
    [InlineData(typeof(WorkflowExecutionCreatedEvent), "utos.execution.created")]
    [InlineData(typeof(WorkflowExecutionScheduledEvent), "utos.execution.scheduled")]
    [InlineData(typeof(WorkflowExecutionActiveEvent), "utos.execution.active")]
    [InlineData(typeof(WorkflowExecutionCompletedEvent), "utos.execution.completed")]
    [InlineData(typeof(WorkflowExecutionFailedEvent), "utos.execution.failed")]
    public void An_event_is_stored_under_the_name_it_declares(Type eventType, string storedName)
    {
        Assert.Equal(storedName, Registry.NameOf(eventType));

        Assert.True(Registry.TryResolve(storedName, out var resolved));
        Assert.Equal(eventType, resolved);
    }

    /// <summary>
    /// Guards the theory above against passing while saying nothing about an event added later:
    /// a sixth event is either named here or it fails this.
    /// </summary>
    [Fact]
    public void Every_event_in_the_module_is_named_above()
    {
        string[] named =
        [
            "utos.execution.active",
            "utos.execution.completed",
            "utos.execution.created",
            "utos.execution.failed",
            "utos.execution.scheduled"
        ];

        Assert.Equal(named, Registry.Names.Order());
    }

    /// <summary>
    /// The prefix the persistent subscription filters on. It is matched server-side and stored
    /// with the group at creation, so a name outside it would not fail — the event would simply
    /// never reach a projection.
    /// </summary>
    [Fact]
    public void Every_name_carries_the_prefix_the_subscription_filters_on()
    {
        Assert.All(Registry.Names, name => Assert.StartsWith("utos.execution.", name));
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
