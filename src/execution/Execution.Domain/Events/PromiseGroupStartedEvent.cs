using Common.SeedWork;

namespace Execution.Domain.Events;

public record PromiseBranchInfo(string BranchName, Guid ChildExecutionId);

public record PromiseGroupStartedEvent(
    Guid ParentExecutionId,
    string ActivityName,
    string Mode,
    int? RequiredCount,
    IReadOnlyList<PromiseBranchInfo> Branches,
    DateTime DateOccurred) : IDomainEvent;
