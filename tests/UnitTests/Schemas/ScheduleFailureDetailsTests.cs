using Execution.Api.Services;
using Execution.Domain.Schemas;
using Common.Workflow;
using Grpc.Core;
using Rpc = Google.Rpc;

namespace UnitTests.Schemas;

/// <summary>
/// What a refused schedule puts on the wire: <c>INVALID_ARGUMENT</c> carrying every failing
/// location as <c>google.rpc.BadRequest</c>, per <c>api/docs/workflow-schemas.md</c> § What a
/// failure reports.
/// <para>
/// Read back the way a client reads it, not the way it was written — a mapping asserted only
/// against its own inputs would pass while producing something no client could parse.
/// </para>
/// </summary>
public class ScheduleFailureDetailsTests
{
    private static SchemaValidationFailedException Failure() =>
        new(SchemaValidation.RunInput, SchemaBoundary.Input,
            new SchemaValidationResult(false,
            [
                new SchemaFailure("", "required", "/required"),
                new SchemaFailure("/quality", "maximum", "/properties/quality/maximum")
            ],
            Truncated: 0,
            Value: ValueMap.Empty));

    [Fact]
    public void A_refusal_is_invalid_argument_carrying_every_failing_location()
    {
        var thrown = ExecutionGrpcService.Refuse(Failure());

        Assert.Equal(StatusCode.InvalidArgument, thrown.StatusCode);

        var bad = thrown.GetRpcStatus()!.GetDetail<Rpc.BadRequest>();
        Assert.NotNull(bad);

        var violations = bad.FieldViolations
            .Select(v => (v.Field, v.Reason))
            .ToList();

        // The complete set. Reporting every failure rather than the first is the point of the
        // format, so a dropped one is as much a break as a wrong one.
        Assert.Equal(2, violations.Count);
        Assert.Contains(("", "required"), violations);
        Assert.Contains(("/quality", "maximum"), violations);
    }

    [Fact]
    public void The_contract_fields_land_in_structured_slots_and_the_prose_does_not()
    {
        var bad = ExecutionGrpcService.Refuse(Failure()).GetRpcStatus()!.GetDetail<Rpc.BadRequest>()!;

        var violation = Assert.Single(bad.FieldViolations, v => v.Field == "/quality");

        // field and reason are the two thirds of the triple the conformance corpus asserts.
        Assert.Equal("maximum", violation.Reason);

        // keywordLocation is the third, and is deliberately prose: more than one schema location
        // can legitimately produce the same failure, so the corpus does not pin it.
        Assert.Contains("/properties/quality/maximum", violation.Description);
    }

    [Fact]
    public void The_message_alone_is_a_complete_diagnostic()
    {
        // This is what lets a NativeAOT client skip `Any` entirely: everything a reader needs is
        // in the message, and the structured form is an upgrade rather than the only copy.
        var message = ExecutionGrpcService.Refuse(Failure()).Status.Detail;

        Assert.Contains(SchemaValidation.RunInput, message);
        Assert.Contains("/quality", message);
        Assert.Contains("maximum", message);
        Assert.Contains("required", message);
    }
}
