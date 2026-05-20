using Common.Infrastructure.Projections;
using Common.SeedWork;
using Microsoft.Extensions.Logging;
using Execution.Domain.Events;
using Execution.Infrastructure.Data;
using Execution.Infrastructure.ReadModels;

namespace Execution.Infrastructure.Projections;

public class SubWorkflowCallProjection(
    WorkflowExecutionDbContext dbContext,
    ILogger<SubWorkflowCallProjection> logger)
    : DbProjection(dbContext, logger)
{
    public override bool CanHandle(Type eventType)
    {
        return eventType == typeof(SubWorkflowStartedEvent)
               || eventType == typeof(PromiseGroupStartedEvent);
    }

    protected override async Task ProjectEventAsync(IDomainEvent @event, long position,
        CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case SubWorkflowStartedEvent e:
                await HandleSubWorkflowStarted(e);
                break;

            case PromiseGroupStartedEvent e:
                await HandlePromiseGroupStarted(e);
                break;
        }
    }

    private Task HandleSubWorkflowStarted(SubWorkflowStartedEvent @event)
    {
        var row = new SubWorkflowCall
        {
            ParentExecutionId = @event.ParentExecutionId,
            ChildExecutionId = @event.ChildExecutionId,
            ActivityName = @event.ActivityName,
            InvocationType = "call"
        };

        dbContext.SubWorkflowCalls.Add(row);

        logger.LogDebug(
            "Projected SubWorkflowStarted: parent {ParentId}, child {ChildId}, activity {Activity}",
            @event.ParentExecutionId, @event.ChildExecutionId, @event.ActivityName);

        return Task.CompletedTask;
    }

    private Task HandlePromiseGroupStarted(PromiseGroupStartedEvent @event)
    {
        foreach (var branch in @event.Branches)
        {
            var row = new SubWorkflowCall
            {
                ParentExecutionId = @event.ParentExecutionId,
                ChildExecutionId = branch.ChildExecutionId,
                ActivityName = @event.ActivityName,
                InvocationType = "promise_branch",
                PromiseMode = @event.Mode,
                BranchName = branch.BranchName
            };

            dbContext.SubWorkflowCalls.Add(row);
        }

        logger.LogDebug(
            "Projected PromiseGroupStarted: parent {ParentId}, activity {Activity}, {BranchCount} branches",
            @event.ParentExecutionId, @event.ActivityName, @event.Branches.Count);

        return Task.CompletedTask;
    }
}
