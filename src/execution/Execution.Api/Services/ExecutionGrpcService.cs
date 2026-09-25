using Utos.Workflows.V1;
using Common.Blobs;
using Common.SeedWork;
using Common.Workflow.Proto;
using Execution.Api.Extensions;
using Execution.Application.Commands;
using Execution.Domain.Schemas;
using Execution.Application.Exceptions;
using Execution.Application.Queries;
using FluentValidation;
using Google.Protobuf.WellKnownTypes;
using Rpc = Google.Rpc;
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

        RequireWellFormedInput(request.Input);

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
        catch (BlobsNotAdmittedException ex)
        {
            // UTOS-F104, stage two of the three. Reported exactly as the other two stages are, so a
            // client handles all three alike — which is what the spec asks for in as many words.
            throw Refuse(ex);
        }
        catch (SchemaValidationFailedException ex)
        {
            // UTOS-H101 / UTOS-H102. No run exists, so this is INVALID_ARGUMENT rather than a
            // failed execution: there is nothing to inspect afterwards and no partial work to
            // undo.
            throw Refuse(ex);
        }
    }

    /// <summary>
    /// What a run's input must be before anything looks at what it <em>says</em>: a well-formed
    /// <c>WorkflowValue</c> tree. Checked at the transport edge, because it is a property of the
    /// wire form and because the mapping inward would otherwise meet a value with no kind and
    /// throw — which reaches the client as an internal error rather than as the refusal it is.
    /// <para>
    /// <c>UTOS-V101</c> and <c>V102</c> come from <c>Utos.Workflow</c>, shared with the CLI, so a
    /// payload one tool builds is not one the other refuses. Every failure is reported, not the
    /// first, for the reason <see cref="Refuse"/> gives.
    /// </para>
    /// <para>
    /// Stage one of three, and it reports two kinds of rule. <c>UTOS-V1##</c> is a value with no kind
    /// or a number that is not finite. <c>UTOS-F104</c> is a blob whose <em>structure</em> is wrong —
    /// an invariant of § A blob as a value — which the shared validator checks here because it needs
    /// nothing but the value. Whether a blob can be <em>attached</em> is stage two and a different
    /// question: it resolves handles against the daemon's own records, which is a handler's business
    /// and not a transport's (<c>StartExecutionHandler</c>). A blob in an input used to be refused
    /// outright here, as <c>Unimplemented</c>, because there was nowhere for one to go.
    /// </para>
    /// </summary>
    private static void RequireWellFormedInput(ExecutionPayload? input)
    {
        if (input is null || input.Data.Count == 0) return;

        var issues = WorkflowValues.Validate(new WorkflowMap { Fields = { input.Data } });

        if (issues.Count == 0) return;

        // The two specs ask for different things in `reason`, and each gets what it asks for.
        // `workflow-values.md` says the code for a V1## rule; `binary-data.md` § Into a run says one of
        // seven tokens for an F104, and the only one a check with no store can reach is `malformed`.
        throw Refuse(
            "the run's input is not a well-formed value: "
            + string.Join(", ", issues.Select(issue => issue.Code).Distinct().Order()) + ".",
            issues.Select(issue => issue.Code == BlobsNotAdmittedException.Code
                ? (issue.Pointer, BlobAdmissionReason.Malformed.Token(), (string?)issue.Code)
                : (issue.Pointer, issue.Code, null)));
    }

    /// <summary>
    /// A refused schedule, carrying <strong>every</strong> failing location — not the first,
    /// because fixing one problem must not reveal the next.
    /// <para>
    /// The list travels twice, deliberately. As <c>google.rpc.BadRequest</c> in the status
    /// details, which is the standard type for field-level validation failures, so a generic
    /// client renders it without knowing anything about Utos. And in the message, so a client
    /// that reads nothing else still loses no information — only the ability to branch on it
    /// without parsing prose. That second copy is what lets the NativeAOT CLI stay clear of
    /// <c>Any</c>, whose type-URL resolution is not statically analysable.
    /// </para>
    /// </summary>
    internal static RpcException Refuse(SchemaValidationFailedException ex)
    {
        var subject = ex.Boundary == SchemaBoundary.Env ? "environment" : "input";

        return Refuse(
            $"{ex.Code}: the run's {subject} does not satisfy the declared schema.",
            ex.Result.Failures.Select(failure =>
                (failure.InstanceLocation, failure.Keyword, (string?)$"at {failure.KeywordLocation}")),
            ex.Result.Truncated);
    }

    /// <summary>
    /// <c>UTOS-F104</c>, reported in the same shape as the two stages either side of it. The
    /// <c>reason</c> is the spec's token — <c>not_found</c>, <c>intermediate</c> — which is what a
    /// client branches on; <c>field</c> is the blob's JSON Pointer into the input, so a request naming
    /// five bad handles says which five.
    /// </summary>
    internal static RpcException Refuse(BlobsNotAdmittedException ex) =>
        Refuse(
            $"{BlobsNotAdmittedException.Code}: a blob in the run's input cannot be attached.",
            ex.Failures.Select(failure => (failure.Pointer, failure.Reason.Token(), (string?)null)));

    /// <summary>
    /// The shared shape of all three stages a schedule passes, which the spec asks for by name: "All
    /// three are <c>INVALID_ARGUMENT</c> carrying <c>google.rpc.BadRequest</c>, so a client handles
    /// them alike."
    /// </summary>
    private static RpcException Refuse(
        string headline,
        IEnumerable<(string Field, string Reason, string? Description)> violations,
        int truncated = 0)
    {
        var bad = new Rpc.BadRequest();
        var lines = new List<string>();

        foreach (var (field, reason, description) in violations)
        {
            bad.FieldViolations.Add(new Rpc.BadRequest.Types.FieldViolation
            {
                // An empty location is the value itself. Left empty rather than spelled "(root)":
                // `field` is the contract, and the prose belongs in the message.
                Field = field,
                Reason = reason,
                Description = description ?? string.Empty,
            });

            lines.Add($"  {(field.Length == 0 ? "(root)" : field)}: {reason}");
        }

        if (truncated > 0)
            lines.Add($"  … and {truncated} more");

        return new Rpc.Status
        {
            Code = (int)Rpc.Code.InvalidArgument,
            Message = string.Join(Environment.NewLine, lines.Prepend(headline)),
            Details = { Any.Pack(bad) },
        }.ToRpcException();
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
            await mediator.Send(
                new DeleteExecutionCommand(executionId, request.DeleteBlobs), context.CancellationToken);
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
