using Google.Protobuf.WellKnownTypes;
using IntegrationTests.Shared;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IntegrationTests.Executions;

/// <summary>
/// What happens to the branches a promise did not wait for.
///
/// Every one of these asserts an <em>absence</em>, and that is the point. A losing branch that is
/// merely ignored looks identical from the parent to one that was stopped: the promise resolved,
/// the run completed, the result is the winner's either way. The difference is only visible from
/// outside — the loser keeps issuing requests — so each test parks its losing branch behind a
/// durable timer and asserts the endpoint on the far side of that timer was never reached.
///
/// The shape that makes them mean something is the <c>hold</c> timer every parent runs after its
/// promise settles. Without it these tests pass whether or not anything is cancelled, because a
/// terminating root reclaims the context rows of its whole subtree
/// (<c>ExecutionSubtreeReclaimer</c>) and an abandoned branch then dies at its next activity for
/// want of one. That is a broken branch, not a stopped one — it happens only once the root is
/// finished, it takes until the branch's next activity boundary, and it is indistinguishable in a
/// test from cancellation working. Holding the root open past <see cref="BranchDelay"/> is what
/// separates the two: an uncancelled branch has a live context row and a running parent, so it
/// wakes up and calls its endpoint.
///
/// The settle/cancel race is covered elsewhere: <c>Promise_RaceMode_FirstSettlementWins</c> and
/// <c>Promise_AllMode_BranchFailure_Propagates</c> both settle on branches that finish in
/// milliseconds, so their siblings are routinely cancelled after they have already terminated.
/// Those runs still completing is what says a cancellation aimed at a finished branch is not an
/// error.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class PromiseCancellationTests(DaemonTestFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>How long a losing branch is parked before it would call its marker endpoint.</summary>
    private static readonly TimeSpan BranchDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long the root stays alive after the promise settles. Comfortably longer than
    /// <see cref="BranchDelay"/>, so a branch left running has both the time and the surviving
    /// context row it needs to reach the endpoint the assertion is about.
    /// </summary>
    private static readonly TimeSpan HoldOpen = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task A_race_loser_is_cancelled_rather_than_left_running()
    {
        const string winnerPath = "/cancel-race-winner";
        const string loserPath = "/cancel-race-loser";

        StubJson(winnerPath);
        StubJson(loserPath);

        var request = new TestWorkflowBuilder("cancel-race", fixture.MockBaseUrl)
            .HttpGet("winner-call", winnerPath)
            // The loser: parked long enough that the race is decided while it is still waiting.
            .Timer("loser-wait", BranchDelay)
            .OnSuccess("loser-call")
            .HttpGet("loser-call", loserPath)
            .Timer("hold", HoldOpen)
            .Promise("fan-out", "race")
            .Branch("winner", "winner-call")
            .Branch("loser", "loser-wait")
            .OnSuccess("hold")
            .Build("fan-out");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        await AssertStatusAsync(scheduled.Id, ExecutionStatus.Completed);
        Assert.Equal(1, CallsTo(winnerPath));
        Assert.Equal(0, CallsTo(loserPath));
    }

    [Fact]
    public async Task An_any_also_ran_is_cancelled_once_a_branch_has_succeeded()
    {
        const string quickPath = "/cancel-any-quick";
        const string alsoRanPath = "/cancel-any-also-ran";

        StubJson(quickPath);
        StubJson(alsoRanPath);

        var request = new TestWorkflowBuilder("cancel-any", fixture.MockBaseUrl)
            .HttpGet("quick-call", quickPath)
            .Timer("also-ran-wait", BranchDelay)
            .OnSuccess("also-ran-call")
            .HttpGet("also-ran-call", alsoRanPath)
            .Timer("hold", HoldOpen)
            .Promise("fan-out", "any")
            .Branch("quick", "quick-call")
            .Branch("also-ran", "also-ran-wait")
            .OnSuccess("hold")
            .Build("fan-out");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        await AssertStatusAsync(scheduled.Id, ExecutionStatus.Completed);
        Assert.Equal(1, CallsTo(quickPath));
        Assert.Equal(0, CallsTo(alsoRanPath));
    }

    [Fact]
    public async Task A_count_that_is_already_met_cancels_the_branches_still_outstanding()
    {
        const string firstPath = "/cancel-count-first";
        const string secondPath = "/cancel-count-second";
        const string outstandingPath = "/cancel-count-outstanding";

        StubJson(firstPath);
        StubJson(secondPath);
        StubJson(outstandingPath);

        var request = new TestWorkflowBuilder("cancel-count", fixture.MockBaseUrl)
            .HttpGet("first-call", firstPath)
            .HttpGet("second-call", secondPath)
            .Timer("outstanding-wait", BranchDelay)
            .OnSuccess("outstanding-call")
            .HttpGet("outstanding-call", outstandingPath)
            .Timer("hold", HoldOpen)
            // Two of the three suffice, so the third is still running when the promise resolves.
            .Promise("fan-out", "count", requiredCount: 2)
            .Branch("first", "first-call")
            .Branch("second", "second-call")
            .Branch("outstanding", "outstanding-wait")
            .OnSuccess("hold")
            .Build("fan-out");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        await AssertStatusAsync(scheduled.Id, ExecutionStatus.Completed);
        Assert.Equal(1, CallsTo(firstPath));
        Assert.Equal(1, CallsTo(secondPath));
        Assert.Equal(0, CallsTo(outstandingPath));
    }

    /// <summary>
    /// The failure path is the same rule, not an exception to it. <c>all</c> fails fast, so a
    /// surviving sibling's result can never be read — and it is the sibling, not the branch that
    /// failed, that would otherwise still be spending.
    /// <para>
    /// The promise's failure is caught by an <c>on_failure</c> rule here purely so the root can be
    /// held open long enough for the assertion to mean something. Reaching <c>hold</c> at all is
    /// itself proof the promise failed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failing_all_cancels_the_siblings_that_are_still_running()
    {
        const string failingPath = "/cancel-all-failing";
        const string siblingPath = "/cancel-all-sibling";

        fixture.WireMockServer
            .Given(Request.Create().WithPath(failingPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("Internal Server Error"));

        StubJson(siblingPath);

        var request = new TestWorkflowBuilder("cancel-all", fixture.MockBaseUrl)
            .HttpGet("failing-call", failingPath)
            .Timer("sibling-wait", BranchDelay)
            .OnSuccess("sibling-call")
            .HttpGet("sibling-call", siblingPath)
            .Timer("hold", HoldOpen)
            .Promise("fan-out", "all")
            .Branch("failing", "failing-call")
            .Branch("sibling", "sibling-wait")
            .OnFailure("hold")
            .Build("fan-out");

        var scheduled = await fixture.ScheduleAsync(request);
        await fixture.WatchToCompletionAsync(scheduled.Id, Timeout);

        await AssertStatusAsync(scheduled.Id, ExecutionStatus.Completed);
        Assert.Equal(1, CallsTo(failingPath));
        Assert.Equal(0, CallsTo(siblingPath));
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private async Task AssertStatusAsync(string executionId, ExecutionStatus expected)
    {
        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = executionId });

        Assert.Equal(expected, details.Summary.Status);
    }

    private int CallsTo(string path) =>
        fixture.WireMockServer.LogEntries.Count(entry => entry.RequestMessage.Path == path);

    private void StubJson(string path) =>
        fixture.WireMockServer
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"ok": true}"""));
}
