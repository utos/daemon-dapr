using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// What a promise branch is allowed to point at.
///
/// Every branch runs as a child execution — that is how the driver fans out, and it stays true
/// here. What changed is that arranging it is no longer the author's job: a branch used to have to
/// target a <c>workflow.call</c>, so fanning out over an HTTP request meant wrapping it in a second
/// document that did nothing else.
///
/// The rule was never in the spec. <c>PromiseBranch.target</c> asks only "which activity to start"
/// and no <c>UTOS-*</c> rule constrains its kind, so the daemon was rejecting documents the shared
/// validator accepts — the one divergence the two are supposed to be incapable of.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class PromiseBranchTargetTests(DaemonTestFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task A_promise_can_fan_out_over_a_plain_http_activity()
    {
        const string fetchPath = "/branch-http-fetch";
        StubJson(fetchPath);

        var request = new TestWorkflowBuilder("branch-http", fixture.MockBaseUrl)
            // The branch target, declared before the promise so the chain can reach it.
            .HttpGet("fetch", fetchPath)
            .WithHeader("X-Item", "{{ input.id }}")
            .Promise("fan-out", "all")
            .ForEachBranch("fetch-one", "fetch", "{{ input.ids }}", "id",
                Fields(("id", Value.ForString("{{ id }}"))))
            .OnResult(Fields(("done", Value.ForBool(true))))
            .WithInput("ids", Value.ForList(
                Value.ForString("a"), Value.ForString("b"), Value.ForString("c")))
            .Build("fan-out");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);

        // One branch per item, and each reached the http activity with its own item.
        Assert.Equal(["a", "b", "c"], ItemsFetched(fetchPath).Order().ToList());
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private List<string> ItemsFetched(string path) =>
        fixture.WireMockServer.LogEntries
            .Where(entry => entry.RequestMessage.Path == path)
            .Select(entry => entry.RequestMessage.Headers!
                .First(header => header.Key == "X-Item").Value.First())
            .ToList();

    private static Struct Fields(params (string Key, Value Value)[] fields)
    {
        var result = new Struct();

        foreach (var (key, value) in fields)
            result.Fields.Add(key, value);

        return result;
    }

    private void StubJson(string path) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));
}
