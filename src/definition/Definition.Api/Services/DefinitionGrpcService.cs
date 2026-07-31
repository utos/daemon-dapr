using Common.Workflow.Proto;
using Definition.Application.Commands;
using Definition.Application.Exceptions;
using Definition.Application.Queries;
using FluentValidation;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Mediator;
using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace Definition.Api.Services;

/// <summary>
/// Manages the daemon's local store of workflow definitions. Definitions are pushed here
/// by the CLI; <c>ExecutionService.ScheduleExecution</c> then references them by
/// name + optional version instead of shipping a whole bundle each run.
/// </summary>
public class DefinitionGrpcService(IMediator mediator) : DefinitionService.DefinitionServiceBase
{
    public override async Task<LoadWorkflowResponse> LoadWorkflow(
        LoadWorkflowRequest request, ServerCallContext context)
    {
        if (request.Bundle is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Bundle is required"));

        // Computed here, while the bundle is still in its wire form: the digest is defined over
        // the canonical serialization, and must match what the CLI and registry computed for the
        // same content.
        var digest = request.Bundle.ComputeContentDigest();

        var definition = await Send(
            ct => mediator.Send(new LoadWorkflowCommand(request.Bundle.ToDomain(), digest), ct), context);

        return new LoadWorkflowResponse
        {
            Reference = definition.Identity.ToProto(definition.Digest)
        };
    }

    public override async Task<ListWorkflowsResponse> ListWorkflows(
        ListWorkflowsRequest request, ServerCallContext context)
    {
        var definitions = await Send(ct => mediator.Send(new ListWorkflowsQuery(), ct), context);

        var response = new ListWorkflowsResponse();

        foreach (var definition in definitions)
        {
            var summary = new WorkflowDefinitionSummary
            {
                Reference = definition.Identity.ToProto(definition.Digest),
                LoadedAt = Timestamp.FromDateTime(definition.LoadedAt.ToUniversalTime())
            };

            if (definition.Description is not null)
                summary.Description = definition.Description;

            response.Workflows.Add(summary);
        }

        return response;
    }

    public override async Task<GetWorkflowResponse> GetWorkflow(
        GetWorkflowRequest request, ServerCallContext context)
    {
        var definition = await Send(
            ct => mediator.Send(new GetWorkflowQuery(ToDomainReference(request.Reference)), ct), context);

        return new GetWorkflowResponse
        {
            Reference = definition.Identity.ToProto(definition.Digest),
            Bundle = definition.Bundle.ToProto(),
            LoadedAt = Timestamp.FromDateTime(definition.LoadedAt.ToUniversalTime())
        };
    }

    public override async Task<UnloadWorkflowResponse> UnloadWorkflow(
        UnloadWorkflowRequest request, ServerCallContext context)
    {
        var removed = await Send(
            ct => mediator.Send(new UnloadWorkflowCommand(ToDomainReference(request.Reference)), ct), context);

        return new UnloadWorkflowResponse { Removed = removed };
    }

    private static Common.Workflow.WorkflowReference ToDomainReference(WorkflowReference? reference)
    {
        if (string.IsNullOrEmpty(reference?.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Workflow name is required"));

        return reference.ToDomain();
    }

    /// <summary>
    /// Dispatches to the application layer and translates its failures into gRPC status codes.
    /// </summary>
    private static async Task<TResponse> Send<TResponse>(
        Func<CancellationToken, ValueTask<TResponse>> send, ServerCallContext context)
    {
        try
        {
            return await send(context.CancellationToken);
        }
        catch (ValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (WorkflowDefinitionNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }
}
