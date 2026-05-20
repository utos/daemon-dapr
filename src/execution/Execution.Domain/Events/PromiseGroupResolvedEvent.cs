using Common.SeedWork;

namespace Execution.Domain.Events;

public record PromiseGroupResolvedEvent(
    Guid ParentExecutionId,
    string ActivityName,
    bool Success,
    DateTime DateOccurred) : IDomainEvent;
