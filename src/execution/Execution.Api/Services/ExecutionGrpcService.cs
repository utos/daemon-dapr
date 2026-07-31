using Common.SeedWork;
using Common.Workflow.Proto;
using Execution.Api.Extensions;
using Execution.Application.Commands;
using Execution.Application.Exceptions;
using Execution.Application.Queries;
using FluentValidation;
using Grpc.Core;
using Mediator;
using Utos.Daemon.V1;

namespace Execution.Api.Services;

public class ExecutionGrpcService(IMediator mediator)
    : ExecutionService.ExecutionServiceBase
{
    public override async Task<ScheduleExecutionResponse> ScheduleExecution(
        ScheduleExecutionRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.Workflow?.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Workflow name is required"));

        try
        {
            var command = new ScheduleExecutionCommand(
                request.Workflow.ToDomain(),
                request.Input.ToDictionary(),
                request.StartActivity,
                request.Env.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

            var result = await mediator.Send(command, context.CancellationToken);

            return new ScheduleExecutionResponse
            {
                Id = result.Id.ToString(),
                Status = result.Status.ToGrpc()
            };
        }
        catch (WorkflowDefinitionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<GetExecutionResponse> GetExecution(
        GetExecutionRequest request, ServerCallContext context)
    {
        if (!ExecutionId.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        var details = await mediator.Send(
            new GetExecutionQuery(executionId), context.CancellationToken);

        if (details == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Execution not found"));

        return details.ToGrpc();
    }

    public override async Task<ListExecutionsResponse> ListExecutions(
        ListExecutionsRequest request, ServerCallContext context)
    {
        // The workflow filter is optional; when present, name selects the workflow.
        if (request.Workflow is not null && string.IsNullOrEmpty(request.Workflow.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "workflow.name is required when a workflow filter is set"));

        var summaries = await mediator.Send(
            new ListExecutionsQuery(request.Workflow?.ToDomain()), context.CancellationToken);

        var response = new ListExecutionsResponse();
        response.Executions.AddRange(summaries.Select(s => s.ToGrpc()));

        return response;
    }

    public override async Task<DeleteExecutionResponse> DeleteExecution(
        DeleteExecutionRequest request, ServerCallContext context)
    {
        if (!ExecutionId.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        try
        {
            await mediator.Send(new DeleteExecutionCommand(executionId), context.CancellationToken);
            return new DeleteExecutionResponse();
        }
        catch (ExecutionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ExecutionNotTerminalException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }
}
