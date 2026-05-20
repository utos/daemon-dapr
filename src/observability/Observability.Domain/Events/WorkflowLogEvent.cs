using Common.SeedWork;
using Utos.Daemon.V1;

namespace Observability.Domain.Events;

public record WorkflowLogEvent(
    Guid ExecutionId,
    string Source,
    string? Category,
    LogLevel Level,
    string Message,
    DateTime DateOccurred) : IDomainEvent;
