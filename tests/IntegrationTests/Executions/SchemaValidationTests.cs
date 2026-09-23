using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using IntegrationTests.Shared.Builders;
using Utos.Testing;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using Rpc = Google.Rpc;

namespace IntegrationTests.Executions;

/// <summary>
/// The run-time schema boundaries end to end, through the real daemon —
/// <c>UTOS-H101</c>–<c>UTOS-H106</c> of <c>api/docs/workflow-schemas.md</c>.
/// <para>
/// Unit tests already pin that the interpreter applies the right declaration at the right moment
/// and that the evaluator agrees with the spec's corpus. What only a running daemon can answer is
/// whether a violation survives the trip: whether it reaches the wire as the code it was raised
/// with, whether an <c>onFailure</c> rule can actually read <c>error.details.schema</c>, and
/// whether a declared default is in what the execution records.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class SchemaValidationTests(DaemonTestFixture fixture)
{
    /// <summary>A compiled object schema: one required string, closed.</summary>
    private static Struct Requires(string property) => new()
    {
        Fields =
        {
            ["type"] = Value.ForString("object"),
            ["required"] = Value.ForList(Value.ForString(property)),
            ["properties"] = Value.ForStruct(new Struct
            {
                Fields =
                {
                    [property] = Value.ForStruct(new Struct
                        { Fields = { ["type"] = Value.ForString("string") } })
                }
            }),
            ["unevaluatedProperties"] = Value.ForBool(false)
        }
    };

    // ============================================================================
    // UTOS-H101 / UTOS-H102 — refused at schedule, before a run exists
    // ============================================================================

    [Fact]
    public async Task ScheduleExecution_ThrowsInvalidArgument_WhenTheInputViolatesTheStartActivitySchema()
    {
        var request = new TestWorkflowBuilder("schema-input-refused", fixture.MockBaseUrl)
            .HttpGet("start", "/success")
                .Accepting(Requires("orderId"))
            .Build();

        request.Input.Data["wrong"] = Value.ForString("x").ToWire();

        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("UTOS-H101", error.Status.Detail);
    }

    [Fact]
    public async Task ScheduleExecution_ReportsEveryFailingLocation_AsStructuredDetails()
    {
        // The whole point of the format: fixing one problem must not reveal the next. Read back
        // the way a client reads it — google.rpc.BadRequest, which a generic tool renders without
        // knowing anything about Utos.
        var request = new TestWorkflowBuilder("schema-details", fixture.MockBaseUrl)
            .HttpGet("start", "/success")
                .Accepting(Requires("orderId"))
            .Build();

        request.Input.Data["wrong"] = Value.ForString("x").ToWire();

        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        var bad = error.GetRpcStatus()?.GetDetail<Rpc.BadRequest>();
        Assert.NotNull(bad);

        var violations = bad.FieldViolations.Select(v => (v.Field, v.Reason)).ToList();

        // Both halves: the required property missing, and the one not declared.
        Assert.Contains(("", "required"), violations);
        Assert.Contains(("/wrong", "unevaluatedProperties"), violations);
    }

    [Fact]
    public async Task ScheduleExecution_ThrowsInvalidArgument_WhenARequiredEnvironmentVariableIsMissing()
    {
        var request = new TestWorkflowBuilder("schema-env-refused", fixture.MockBaseUrl)
            .Requiring(Requires("API_BASE"))
            .HttpGet("start", "/success")
            .Build();

        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("UTOS-H102", error.Status.Detail);
    }

    [Fact]
    public async Task ScheduleExecution_RecordsTheFilledDefaults_NotWhatTheCallerSent()
    {
        // A replay reads the recorded input, so the default has to be in it. If it were applied
        // only on the way into the first activity, the run and its record would disagree about
        // what it was given.
        var schema = new Struct
        {
            Fields =
            {
                ["type"] = Value.ForString("object"),
                ["properties"] = Value.ForStruct(new Struct
                {
                    Fields =
                    {
                        ["quality"] = Value.ForStruct(new Struct
                        {
                            Fields =
                            {
                                ["type"] = Value.ForString("integer"),
                                ["default"] = Value.ForNumber(80)
                            }
                        })
                    }
                }),
                ["unevaluatedProperties"] = Value.ForBool(false)
            }
        };

        var request = new TestWorkflowBuilder("schema-defaults", fixture.MockBaseUrl)
            .HttpGet("start", "/success")
                .Accepting(schema)
            .Build();

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(80, execution.Input.Data["quality"].NumberValue);
    }

    // ============================================================================
    // UTOS-H105 — a result that violates spec.output fails the run, uncatchably
    // ============================================================================

    [Fact]
    public async Task AResultThatViolatesTheDeclaredOutput_FailsTheRun()
    {
        var request = new TestWorkflowBuilder("schema-output-violated", fixture.MockBaseUrl)
            .Returning(Requires("receiptId"))
            .HttpGet("start", "/success")
                .OnResult(new Struct { Fields = { ["delivered"] = Value.ForBool(true) } })
            .Build();

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(60));

        var terminal = events.Last(e => e.Status == ExecutionStatus.Failed);
        Assert.Equal("UTOS-H105", terminal.Error.Code);
    }

    // ============================================================================
    // UTOS-H104 — the one run-time boundary an onFailure rule may catch
    // ============================================================================

    [Fact]
    public async Task AnInvocationRefusedByTheCallee_IsCatchableByTheCallingActivity()
    {
        // The claim the whole catchability table rests on. H104 is the calling activity failing,
        // so its own onFailure rules apply — and the rule can branch on the code, which is what
        // makes a coded failure worth more than a message.
        var request = new TestWorkflowBuilder("schema-invocation-caught", fixture.MockBaseUrl)
            .WorkflowCall("call", "schema-child-workflow", "receive",
                new Struct { Fields = { ["orderId"] = Value.ForString("{{ input.count }}") } })
                .OnFailure("recover", when: "error.code === 'UTOS-H104'")
            .HttpGet("recover", "/success")
                .OnResult(new Struct { Fields = { ["recovered"] = Value.ForBool(true) } })
            .AddDependentWorkflow("schema-child-workflow", child => child
                .HttpGet("receive", "/success")
                    .Accepting(Requires("orderId"))
                .OnEnd())
            .WithInput("count", 42)
            .Build("call");

        var response = await fixture.ScheduleAsync(request);

        var events = await fixture.ObservabilityClient
            .WatchExecution(new WatchExecutionRequest { ExecutionId = response.Id })
            .CollectUntilCompletion(TimeSpan.FromSeconds(60));

        // Caught, so the run completes rather than failing — and it reached the handler.
        Assert.Equal(ExecutionStatus.Completed, TerminalStatus(events));

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.True(execution.Result.Fields["recovered"].BoolValue);
    }

    [Fact]
    public async Task ACaughtInvocationFailure_ExposesEveryFailingLocation_AsErrorDetailsSchema()
    {
        // `error.details.schema` is the reason the failure list is structured rather than prose:
        // a rule can carry which field was wrong into the run's own result.
        var request = new TestWorkflowBuilder("schema-details-in-scope", fixture.MockBaseUrl)
            .WorkflowCall("call", "schema-detail-child", "receive",
                new Struct { Fields = { ["orderId"] = Value.ForString("{{ input.count }}") } })
                .OnFailureResult(new Struct
                {
                    Fields =
                    {
                        ["boundary"] = Value.ForString("{{ error.details.schema.boundary }}"),
                        ["keyword"] = Value.ForString("{{ error.details.schema.errors[0].keyword }}"),
                        ["where"] = Value.ForString("{{ error.details.schema.errors[0].instanceLocation }}")
                    }
                })
            .AddDependentWorkflow("schema-detail-child", child => child
                .HttpGet("receive", "/success")
                    .Accepting(Requires("orderId"))
                .OnEnd())
            .WithInput("count", 42)
            .Build("call");

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id, TimeSpan.FromSeconds(60));

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        var result = execution.Result.Fields;

        Assert.Equal("input", result["boundary"].StringValue);

        // The callee declares orderId as a string and the transform rendered a number, which is
        // knowable only once input.count exists — the load-time rule compares property sets.
        Assert.Equal("type", result["keyword"].StringValue);
        Assert.Equal("/orderId", result["where"].StringValue);
    }

    // ============================================================================
    // The path every workflow written before schemas existed takes
    // ============================================================================

    [Fact]
    public async Task AWorkflowThatDeclaresNothing_RunsUnchanged()
    {
        var request = new TestWorkflowBuilder("schema-absent", fixture.MockBaseUrl)
            .HttpGet("start", "/success")
                .OnResult(new Struct { Fields = { ["ok"] = Value.ForBool(true) } })
            .Build();

        request.Input.Data["anything"] = Value.ForString("at all").ToWire();

        var response = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(response.Id);

        var execution = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = response.Id });

        Assert.Equal(ExecutionStatus.Completed, execution.Summary.Status);
        Assert.True(execution.Result.Fields["ok"].BoolValue);
    }

    private static ExecutionStatus TerminalStatus(IReadOnlyList<WatchExecutionResponse> events) =>
        events.Last(e => e.Status is ExecutionStatus.Completed or ExecutionStatus.Failed
            or ExecutionStatus.Cancelled).Status;
}
