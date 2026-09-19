using Common.SeedWork;
using Common.Workflow.Proto;
using Execution.Api.Extensions;
using Execution.Application.Commands;
using Execution.Domain.Schemas;
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
        catch (SchemaValidationFailedException ex)
        {
            // UTOS-H101 / UTOS-H102. No run exists, so this is INVALID_ARGUMENT rather than a
            // failed execution: there is nothing to inspect afterwards and no partial work to
            // undo.
            //
            // Every failing location is listed, not the first, because that is the point of the
            // format — fixing one problem must not reveal the next. They go in the message
            // rather than in google.rpc.Status details, which is where the spec puts them; that
            // is a gap, and the difference is machine-readability, not completeness.
            throw new RpcException(new Status(StatusCode.InvalidArgument, Describe(ex)));
        }
    }

    /// <summary>
    /// The complete failure list, one location per line, as the code and the locations are what a
    /// caller acts on. The code prefix is the contract; the prose around it deliberately is not.
    /// </summary>
    private static string Describe(SchemaValidationFailedException ex)
    {
        var lines = ex.Result.Failures.Select(f =>
            $"  {(f.InstanceLocation.Length == 0 ? "(root)" : f.InstanceLocation)}: {f.Keyword}");

        var body = string.Join(Environment.NewLine, lines);

        if (ex.Result.Truncated > 0)
            body += Environment.NewLine + $"  … and {ex.Result.Truncated} more";

        var subject = ex.Boundary == SchemaBoundary.Env ? "environment" : "input";

        return $"{ex.Code}: the run's {subject} does not satisfy the declared schema."
               + Environment.NewLine + body;
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

    public override async Task WatchOutput(
        WatchOutputRequest request,
        IServerStreamWriter<WatchOutputResponse> responseStream,
        ServerCallContext context)
    {
        if (!ExecutionId.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        // 'tail' and 'after' are a oneof, so the wire format already makes them exclusive.
        var query = new WatchExecutionOutputQuery(
            executionId,
            request.HasAfter ? request.After : null,
            request.HasTail ? request.Tail : null);

        try
        {
            await foreach (var entry in mediator.CreateStream(query, context.CancellationToken))
                await responseStream.WriteAsync(entry.ToGrpc());
        }
        catch (ExecutionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (OperationCanceledException)
        {
            // The client stopped reading. That is how a follow ends, not a fault.
        }
    }

    public override async Task<CancelExecutionResponse> CancelExecution(
        CancelExecutionRequest request, ServerCallContext context)
    {
        if (!ExecutionId.TryParse(request.ExecutionId, out var executionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid execution ID format"));

        try
        {
            await mediator.Send(
                new CancelExecutionCommand(executionId, request.HasReason ? request.Reason : null),
                context.CancellationToken);

            return new CancelExecutionResponse { Status = ExecutionStatus.Cancelled };
        }
        catch (ExecutionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ExecutionAlreadyTerminalException ex)
        {
            // The run reached a different terminal state first, and that one wins. Reported
            // rather than smoothed into a success, so a caller is not told it stopped something
            // that had already finished on its own.
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
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
