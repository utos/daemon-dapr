using System.Text;
using Blobs.Domain;
using Blobs.Infrastructure.Data;
using Blobs.Infrastructure.Store;
using Common.SeedWork;
using Microsoft.EntityFrameworkCore;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using Utos.Workflows.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Rpc = Google.Rpc;
using WireBlob = Utos.Workflows.V1.Blob;

namespace IntegrationTests.Storage;

/// <summary>
/// A blob a client puts into a run — <c>api/docs/binary-data.md</c> § Into a run, the second of the
/// three stages a schedule passes.
/// <para>
/// The first test here is the loop closing. Everything before it moved blobs <em>within</em> the
/// daemon or out of it; this is a handle leaving one run and going back into another, which is what a
/// person actually does with binary data — fetch it once, then run something over it. A blob in an
/// input was refused outright with <c>Unimplemented</c> until now.
/// </para>
/// <para>
/// The refusals are asserted by <b>reason token</b>, never by wording. The token is what a client
/// branches on: <c>intermediate</c> means "carry it out of the run that made it", <c>too_large</c>
/// means "upload it instead". A daemon answering <c>not_found</c> to all of them would be conformant
/// in code and useless in practice.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobIntoARunTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task A_blob_one_run_returned_can_be_read_by_the_next()
    {
        const string marker = "-=END=-";
        var body = new string('q', 400 * 1024) + marker;

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/into-a-run").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(body));

        // Run one fetches it and returns it, so promotion makes it durable — which is exactly the
        // precondition a client's attach has. An intermediate would be refused, and the test below
        // proves it.
        var producer = new TestWorkflowBuilder("blob-producer", fixture.MockBaseUrl)
            .HttpGet("fetch", "/into-a-run")
            .OnResult(new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } })
            .Build("fetch");

        var first = await fixture.ScheduleAsync(producer);
        await fixture.WatchToCompletionAsync(first.Id, TimeSpan.FromSeconds(120));

        var handle = (await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = first.Id })).Result.Fields["photo"].BlobValue;

        Assert.NotEmpty(handle.Id);

        // Run two is given the handle exactly as the client received it. A client must never construct
        // one, and it does not have to: this is the same message, passed on.
        var result = await Consume("blob-consumer", handle, new Struct
        {
            Fields =
            {
                ["size"] = Value.ForString("{{ input.photo.size }}"),
                ["type"] = Value.ForString("{{ input.photo.type }}"),
                ["tail"] = Value.ForString("{{ (await input.photo.text()).slice(-7) }}"),
            },
        });

        Assert.Equal(body.Length, (long)result.Fields["size"].NumberValue);
        Assert.Equal("text/plain", result.Fields["type"].StringValue);

        // The bytes came back out of the store, in a run tree that had never seen them before.
        Assert.Equal(marker, result.Fields["tail"].StringValue);
    }

    [Fact]
    public async Task A_client_can_put_small_bytes_in_without_a_round_trip()
    {
        // § Into a run's second form. No store is needed for this at all, which is what makes it the
        // one that works on a daemon configured without one.
        var result = await Consume(
            "inline-input",
            new WireBlob
            {
                Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes("inline bytes")),
                Size = 12,
                MediaType = "text/plain",
            },
            new Struct
            {
                Fields =
                {
                    ["text"] = Value.ForString("{{ await input.photo.text() }}"),
                    ["size"] = Value.ForString("{{ input.photo.size }}"),
                },
            });

        Assert.Equal("inline bytes", result.Fields["text"].StringValue);
        Assert.Equal(12, (long)result.Fields["size"].NumberValue);
    }

    [Fact]
    public async Task A_named_inline_blob_arrives_as_a_File()
    {
        // `name` present is the whole of what makes a blob a File, and it survives the wire, the
        // schedule and the scope.
        var result = await Consume(
            "inline-file-input",
            new WireBlob
            {
                Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes("csv,data")),
                Size = 8,
                MediaType = "text/csv",
                Name = "orders.csv",
            },
            new Struct
            {
                Fields =
                {
                    ["name"] = Value.ForString("{{ input.photo.name }}"),
                    ["isFile"] = Value.ForString("{{ input.photo instanceof File }}"),
                },
            });

        Assert.Equal("orders.csv", result.Fields["name"].StringValue);
        Assert.True(result.Fields["isFile"].BoolValue);
    }

    [Fact]
    public async Task A_spawn_carries_an_intermediate_into_the_new_tree_it_starts()
    {
        // § Access's third way a blob joins a run tree, and the one place durability is *not* required.
        // A `workflow.spawn` starts a new tree, so the blob has to be attached to it — but an
        // intermediate the spawning tree produced is a value the parent legitimately holds, and handing
        // it to a run the parent starts is passing a value on rather than reaching for somebody else's
        // object. A client attaching the same blob would be refused (`intermediate`, above).
        const string marker = "-=END=-";
        var body = new string('s', 400 * 1024) + marker;

        fixture.WireMockServer
            .Given(Request.Create().WithPath("/spawn-source").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(body));

        // Two hops, because they are different scopes: the transition carries the response's body into
        // the spawn activity's `input`, and the spawn's own `input` is rendered there — where there is
        // no `response` to read, the spawn having made no request of its own.
        var request = new TestWorkflowBuilder("spawn-with-blob", fixture.MockBaseUrl)
            .HttpGet("fetch", "/spawn-source")
            .OnSuccess("hand-over",
                new Struct { Fields = { ["photo"] = Value.ForString("{{ response.body }}") } })
            .WorkflowSpawn("hand-over", "spawn-blob-child", "read",
                new Struct { Fields = { ["photo"] = Value.ForString("{{ input.photo }}") } })
            .OnResult(new Struct { Fields = { ["child"] = Value.ForString("{{ output.execution_id }}") } })
            .AddDependentWorkflow("spawn-blob-child", child =>
                child.Timer("read", TimeSpan.FromMilliseconds(1))
                    .OnResult(new Struct
                    {
                        Fields =
                        {
                            ["tail"] = Value.ForString("{{ (await input.photo.text()).slice(-7) }}"),
                            ["size"] = Value.ForString("{{ input.photo.size }}"),
                        },
                    }))
            .Build("fetch");

        var parent = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(parent.Id, TimeSpan.FromSeconds(120));

        var spawner = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = parent.Id });

        // Asserted before the result is read, so a parent that failed says why rather than throwing a
        // NullReferenceException on a result it never produced.
        Assert.Equal(ExecutionStatus.Completed, spawner.Summary.Status);
        Assert.Null(spawner.Summary.Error);

        var childId = spawner.Result.Fields["child"].StringValue;

        Assert.NotEmpty(childId);

        await fixture.WatchToCompletionAsync(childId, TimeSpan.FromSeconds(120));

        var child = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = childId });

        Assert.Equal(ExecutionStatus.Completed, child.Summary.Status);
        Assert.Equal(marker, child.Result.Fields["tail"].StringValue);
        Assert.Equal(body.Length, (long)child.Result.Fields["size"].NumberValue);

        // It belongs to both trees now, which is what the spec says attaching at a spawn does.
        var held = await BlobsOf(childId);
        Assert.Single(held);
        Assert.Contains(held[0], await BlobsOf(parent.Id));
    }

    [Fact]
    public async Task An_unknown_id_is_refused_as_not_found()
    {
        await Refused(
            new WireBlob { Id = "b_00000000000nothingxyz", Size = 10 },
            "/photo", "not_found");
    }

    [Fact]
    public async Task A_blob_another_run_produced_and_did_not_return_is_refused_as_intermediate()
    {
        // The reason the distinction exists. This blob is real, readable and in the bucket — it simply
        // belongs to a run that did not carry it out, so the daemon is entitled to remove it and will
        // not promise otherwise.
        fixture.WireMockServer
            .Given(Request.Create().WithPath("/not-carried-out").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/plain")
                .WithBody(new string('i', 400 * 1024)));

        var producer = new TestWorkflowBuilder("blob-kept-in", fixture.MockBaseUrl)
            .HttpGet("fetch", "/not-carried-out")
            .OnResult(new Struct { Fields = { ["size"] = Value.ForString("{{ response.body.size }}") } })
            .Build("fetch");

        var run = await fixture.ScheduleAsync(producer);
        await fixture.WatchToCompletionAsync(run.Id, TimeSpan.FromSeconds(120));

        // Found the way an operator would, since nothing the client can see names it.
        var produced = Assert.Single(await BlobsOf(run.Id));

        await Refused(
            new WireBlob { Id = produced.Value, Size = 400 * 1024 },
            "/photo", "intermediate");
    }

    [Fact]
    public async Task An_inline_blob_over_the_inline_threshold_is_refused_as_too_large()
    {
        // Refused rather than quietly stored on the client's behalf: the daemon would be inventing a
        // durable blob nobody asked for, and the client would have no id for it.
        var bytes = new byte[512 * 1024];

        await Refused(
            new WireBlob { Data = ByteString.CopyFrom(bytes), Size = bytes.Length },
            "/photo", "too_large");
    }

    [Fact]
    public async Task A_handle_with_no_backing_at_all_is_refused_as_malformed()
    {
        // The shape of a handle a client built itself, which the spec forbids it from doing. Neither
        // `data` nor `id` means there is nothing to read and nothing to resolve.
        //
        // Caught in stage *one*, by the validator shared with the CLI, because it is a property of the
        // value and needs no store. The reason is still the spec's token rather than the code the
        // validator reports it under: § Into a run names seven tokens and `malformed` is this one.
        await Refused(new WireBlob { Size = 10 }, "/photo", "malformed");
    }

    [Fact]
    public async Task Every_unattachable_blob_is_named_by_its_own_pointer()
    {
        // Fixing one problem must not reveal the next, and stage two reports like the two either side
        // of it — INVALID_ARGUMENT carrying google.rpc.BadRequest, which the spec asks for by name so
        // that a client handles all three alike.
        var request = Workflow("many-unknown-blobs");

        request.Input.Data["photo"] = Blob(new WireBlob { Id = "b_nothing", Size = 1 });
        request.Input.Data["scan"] = Blob(new WireBlob { Id = "b_alsonothing", Size = 1 });

        var violations = await Violations(request);

        Assert.Equal(
            new Dictionary<string, string> { ["/photo"] = "not_found", ["/scan"] = "not_found" },
            violations);
    }

    [Fact]
    public async Task A_value_with_no_kind_is_refused_under_its_own_code()
    {
        // Stage one's other half, and not blob-specific: `workflow-values.md` § Well-formed values says
        // `reason` is *the code* for a UTOS-V1## rule, where § Into a run says a token for UTOS-F104. The
        // two specs ask for different things in the same field and each gets what it asks for, which is
        // the only reason this test sits beside the blob ones.
        var request = Workflow("valueless-input");

        request.Input.Data["nothing"] = new WorkflowValue();

        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("UTOS-V101", error.Status.Detail);

        var violation = Assert.Single(
            error.GetRpcStatus()?.GetDetail<Rpc.BadRequest>()?.FieldViolations
            ?? throw new InvalidOperationException("the refusal carried no BadRequest"));

        Assert.Equal("/nothing", violation.Field);
        Assert.Equal("UTOS-V101", violation.Reason);
    }

    [Fact]
    public async Task A_stage_one_failure_hides_the_stage_two_one_behind_it()
    {
        // "A request that fails one stage is refused with every failure of that stage, before the next
        // runs." So a malformed blob beside an unknown id reports only the malformed one — the unknown
        // id has not been looked up, and saying so would mean resolving handles for a request that is
        // being refused anyway.
        var request = Workflow("staged-blob-failures");

        request.Input.Data["photo"] = Blob(new WireBlob { Id = "b_nothing", Size = 1 });
        request.Input.Data["scan"] = Blob(new WireBlob { Size = 1 });

        var violations = await Violations(request);

        Assert.Equal(new Dictionary<string, string> { ["/scan"] = "malformed" }, violations);
    }

    /// <summary>
    /// Schedules a run whose input holds <paramref name="blob"/> at <c>/photo</c> and returns what it
    /// produced. A timer, not an HTTP activity, so nothing here depends on a mocked response — what is
    /// under test is the input.
    /// </summary>
    private async Task<Struct> Consume(string name, WireBlob blob, Struct result)
    {
        var request = Workflow(name, result);
        request.Input.Data["photo"] = Blob(blob);

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, TimeSpan.FromSeconds(120));

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        return ToStruct(details.Result);
    }

    private async Task Refused(WireBlob blob, string pointer, string reason)
    {
        // The reason is in the name only to keep the workflows distinct, and it is slugged: a workflow
        // name is validated on load (UTOS-M003) and `not_found` is not a legal one.
        var request = Workflow($"refused-{reason.Replace('_', '-')}");
        request.Input.Data["photo"] = Blob(blob);

        Assert.Equal(new Dictionary<string, string> { [pointer] = reason }, await Violations(request));
    }

    /// <summary>
    /// The field violations a refused schedule carries, read back the way a client reads them. The
    /// status is always <c>INVALID_ARGUMENT</c> and the message always names <c>UTOS-F104</c>, so those
    /// are asserted here rather than at each call.
    /// </summary>
    private async Task<Dictionary<string, string>> Violations(TestWorkflowRequest request)
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("UTOS-F104", error.Status.Detail);

        var bad = error.GetRpcStatus()?.GetDetail<Rpc.BadRequest>()
                  ?? throw new InvalidOperationException("the refusal carried no BadRequest");

        return bad.FieldViolations.ToDictionary(
            violation => violation.Field, violation => violation.Reason);
    }

    private TestWorkflowRequest Workflow(string name, Struct? result = null)
    {
        var builder = new TestWorkflowBuilder(name, fixture.MockBaseUrl)
            .Timer("hold", TimeSpan.FromMilliseconds(1));

        return (result is null ? builder : builder.OnResult(result)).Build("hold");
    }

    private static WorkflowValue Blob(WireBlob blob) => new() { BlobValue = blob };

    /// <summary>The blobs one run tree holds, for the cases where the client can see no handle.</summary>
    private async Task<IReadOnlyList<BlobId>> BlobsOf(string executionId)
    {
        var repository = new EfBlobRepository(new BlobsDbContext(
            new DbContextOptionsBuilder<BlobsDbContext>()
                .UseNpgsql(fixture.DatabaseConnectionString)
                .Options));

        var held = await repository.FindRunTreeBlobsAsync(
            ExecutionId.Parse(executionId), CancellationToken.None);

        return held.Select(entry => entry.Blob.Id).ToList();
    }

    /// <summary>
    /// A <c>WorkflowMap</c> as a <c>Struct</c>, so the assertions read like every other test's. Only
    /// used for results that hold no blob, which is every one here.
    /// </summary>
    private static Struct ToStruct(WorkflowMap map)
    {
        var result = new Struct();

        foreach (var (key, value) in map.Fields)
            result.Fields[key] = value.KindCase switch
            {
                WorkflowValue.KindOneofCase.StringValue => Value.ForString(value.StringValue),
                WorkflowValue.KindOneofCase.NumberValue => Value.ForNumber(value.NumberValue),
                WorkflowValue.KindOneofCase.BoolValue => Value.ForBool(value.BoolValue),
                _ => Value.ForNull(),
            };

        return result;
    }
}
