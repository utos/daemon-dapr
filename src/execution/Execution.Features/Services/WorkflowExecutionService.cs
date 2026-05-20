using Execution.Features.Extensions;
using Execution.Features.Commands;
using Execution.Features.Queries;
using FluentValidation;
using Grpc.Core;
using Mediator;
using Utos.Daemon.V1;

namespace Execution.Features.Services;

public class ExecutionGrpcService(IMediator mediator) : ExecutionService.ExecutionServiceBase
{
    public override async Task<ScheduleExecutionResponse> ScheduleExecution(
        ScheduleExecutionRequest request, ServerCallContext context)
    {
        try
        {
            var command = new ScheduleExecutionCommand(
                request.Bundle,
                request.Input.ToDictionary(),
                request.StartActivity);

            var result = await mediator.Send(command, context.CancellationToken);

            return new ScheduleExecutionResponse
            {
                Id = result.Id.ToString(),
                Status = result.Status
            };
        }
        catch (ValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<GetExecutionResponse> GetExecution(
        GetExecutionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ExecutionId, out var executionId))
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
        var summaries = await mediator.Send(
            new ListExecutionsQuery(), context.CancellationToken);

        var response = new ListExecutionsResponse();
        response.Executions.AddRange(summaries.Select(s => s.ToGrpc()));

        return response;
    }
}
