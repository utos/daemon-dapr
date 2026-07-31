using Common.Workflow.Proto;
using Definition.Domain;
using FluentValidation;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Utos.Daemon.V1;
using Utos.Workflow.V1;

namespace Definition.Infrastructure.Services;

/// <summary>
/// Manages the daemon's local store of workflow definitions. Definitions are pushed here
/// by the CLI; <c>ExecutionService.ScheduleExecution</c> then references them by
/// name + optional version instead of shipping a whole bundle each run.
/// </summary>
public class DefinitionGrpcService(
    IWorkflowDefinitionStore store,
    IValidator<Common.Workflow.WorkflowBundle> bundleValidator,
    ILogger<DefinitionGrpcService> logger)
    : DefinitionService.DefinitionServiceBase
{
    public override Task<LoadWorkflowResponse> LoadWorkflow(
        LoadWorkflowRequest request, ServerCallContext context)
    {
        if (request.Bundle is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Bundle is required"));

        // Computed here, while the bundle is still in its wire form: the digest is defined over
        // the canonical serialization, and must match what the CLI and registry computed for the
        // same content.
        var digest = request.Bundle.ComputeContentDigest();
        var bundle = request.Bundle.ToDomain();

        var validation = bundleValidator.Validate(bundle);
        if (!validation.IsValid)
            throw new RpcException(new Status(StatusCode.InvalidArgument, validation.ToString()));

        var definition = store.Load(bundle, digest);

        logger.LogInformation("Loaded workflow definition {WorkflowIdentity} ({WorkflowDigest})",
            definition.Identity, definition.Digest);

        return Task.FromResult(new LoadWorkflowResponse
        {
            Reference = definition.Identity.ToProto(definition.Digest)
        });
    }

    public override Task<ListWorkflowsResponse> ListWorkflows(
        ListWorkflowsRequest request, ServerCallContext context)
    {
        var response = new ListWorkflowsResponse();

        foreach (var definition in store.List())
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

        return Task.FromResult(response);
    }

    public override Task<GetWorkflowResponse> GetWorkflow(
        GetWorkflowRequest request, ServerCallContext context)
    {
        var definition = Resolve(request.Reference);

        return Task.FromResult(new GetWorkflowResponse
        {
            Reference = definition.Identity.ToProto(definition.Digest),
            Bundle = definition.Bundle.ToProto(),
            LoadedAt = Timestamp.FromDateTime(definition.LoadedAt.ToUniversalTime())
        });
    }

    public override Task<UnloadWorkflowResponse> UnloadWorkflow(
        UnloadWorkflowRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.Reference?.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Workflow name is required"));

        var removed = store.Remove(request.Reference.ToDomain());

        logger.LogInformation("Unloaded {RemovedCount} workflow definition(s) for '{WorkflowName}'",
            removed, request.Reference.Name);

        return Task.FromResult(new UnloadWorkflowResponse { Removed = removed });
    }

    private LoadedDefinition Resolve(WorkflowReference? reference)
    {
        if (string.IsNullOrEmpty(reference?.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Workflow name is required"));

        var resolved = reference.ToDomain();

        return store.Resolve(resolved)
               ?? throw new RpcException(new Status(StatusCode.NotFound,
                   $"Workflow definition '{resolved}' not found"));
    }
}
