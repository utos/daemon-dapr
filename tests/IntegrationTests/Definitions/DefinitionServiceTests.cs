using Grpc.Core;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;

namespace IntegrationTests.Definitions;

[Collection(DaemonTestCollection.Name)]
public class DefinitionServiceTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task LoadWorkflow_ReturnsResolvedReferenceWithDigest()
    {
        // Arrange
        var request = new TestWorkflowBuilder("def-load-test", fixture.MockBaseUrl, "1.4.2")
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var response = await fixture.DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = request.Bundle });

        // Assert
        Assert.Equal("def-load-test", response.Reference.Name);
        Assert.Equal("1.4.2", response.Reference.Version);
        Assert.StartsWith("sha256:", response.Reference.Digest);
    }

    [Fact]
    public async Task LoadWorkflow_IsContentAddressed_SameContentYieldsSameDigest()
    {
        // Arrange - two structurally identical bundles
        var first = new TestWorkflowBuilder("def-digest-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();
        var second = new TestWorkflowBuilder("def-digest-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act
        var a = await fixture.DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = first.Bundle });
        var b = await fixture.DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = second.Bundle });

        // Assert - re-loading identical content is a replace, not a new identity
        Assert.Equal(a.Reference.Digest, b.Reference.Digest);
    }

    [Fact]
    public async Task LoadWorkflow_ThrowsInvalidArgument_WhenBundleIsInvalid()
    {
        // Arrange - an entry point that names no workflow in the bundle
        var request = new TestWorkflowBuilder("def-invalid-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();
        request.Bundle.EntryPoint = "absent:9.9.9";

        // Act & Assert - the definition module runs the document's shape rules itself; before it
        // registered them, this only worked because the execution module happened to be present.
        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.DefinitionClient.LoadWorkflowAsync(
                new LoadWorkflowRequest { Bundle = request.Bundle }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        // UTOS-B003: entry_point must be a key of workflows. The code is the contract; the
        // wording deliberately is not (api/docs/workflow-validation.md).
        Assert.Contains("UTOS-B003", exception.Status.Detail);
    }

    [Fact]
    public async Task ListWorkflows_IncludesLoadedDefinition()
    {
        // Arrange
        var request = new TestWorkflowBuilder("def-list-test", fixture.MockBaseUrl, "2.0.0",
                "Definition list test")
            .HttpGet("valid-activity", "/success")
            .Build();
        await fixture.DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = request.Bundle });

        // Act
        var response = await fixture.DefinitionClient.ListWorkflowsAsync(new ListWorkflowsRequest());

        // Assert
        var entry = Assert.Single(response.Workflows,
            w => w.Reference.Name == "def-list-test" && w.Reference.Version == "2.0.0");
        Assert.Equal("Definition list test", entry.Description);
        Assert.StartsWith("sha256:", entry.Reference.Digest);
        Assert.NotNull(entry.LoadedAt);
    }

    [Fact]
    public async Task GetWorkflow_ReturnsBundleAsLoaded()
    {
        // Arrange
        var request = new TestWorkflowBuilder("def-get-test", fixture.MockBaseUrl)
            .HttpGet("only-activity", "/success")
            .Build();
        var loaded = await fixture.DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = request.Bundle });

        // Act
        var response = await fixture.DefinitionClient.GetWorkflowAsync(new GetWorkflowRequest
        {
            Reference = new WorkflowReference { Name = "def-get-test" }
        });

        // Assert
        Assert.Equal(loaded.Reference.Digest, response.Reference.Digest);
        var workflow = response.Bundle.Workflows[response.Bundle.EntryPoint];
        Assert.Equal("def-get-test", workflow.Metadata.Name);
        Assert.True(workflow.Spec.Activities.ContainsKey("only-activity"));
    }

    [Fact]
    public async Task GetWorkflow_OmittedVersion_ResolvesHighestSemver()
    {
        // Arrange - load out of order so "latest" cannot come from insertion order,
        // and 10 > 9 only under numeric comparison
        foreach (var version in new[] { "1.9.0", "1.10.0", "1.2.0" })
            await fixture.DefinitionClient.LoadWorkflowAsync(new LoadWorkflowRequest
            {
                Bundle = new TestWorkflowBuilder("def-latest-test", fixture.MockBaseUrl, version)
                    .HttpGet("valid-activity", "/success")
                    .Build().Bundle
            });

        // Act
        var response = await fixture.DefinitionClient.GetWorkflowAsync(new GetWorkflowRequest
        {
            Reference = new WorkflowReference { Name = "def-latest-test" }
        });

        // Assert
        Assert.Equal("1.10.0", response.Reference.Version);
    }

    [Fact]
    public async Task GetWorkflow_ThrowsNotFound_WhenNotLoaded()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.DefinitionClient.GetWorkflowAsync(new GetWorkflowRequest
            {
                Reference = new WorkflowReference { Name = "def-never-loaded" }
            }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task GetWorkflow_ThrowsNotFound_WhenDigestGuardMismatches()
    {
        // Arrange
        var request = new TestWorkflowBuilder("def-digest-guard-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();
        await fixture.DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = request.Bundle });

        // Act & Assert - digest is a guard: the name resolves, then the content must match
        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.DefinitionClient.GetWorkflowAsync(new GetWorkflowRequest
            {
                Reference = new WorkflowReference
                {
                    Name = "def-digest-guard-test",
                    Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
                }
            }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task UnloadWorkflow_OmittedVersion_RemovesAllVersions()
    {
        // Arrange
        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
            await fixture.DefinitionClient.LoadWorkflowAsync(new LoadWorkflowRequest
            {
                Bundle = new TestWorkflowBuilder("def-unload-all-test", fixture.MockBaseUrl, version)
                    .HttpGet("valid-activity", "/success")
                    .Build().Bundle
            });

        // Act
        var response = await fixture.DefinitionClient.UnloadWorkflowAsync(new UnloadWorkflowRequest
        {
            Reference = new WorkflowReference { Name = "def-unload-all-test" }
        });

        // Assert - removing only the latest could leave an older version silently in place
        Assert.Equal(3, response.Removed);

        var list = await fixture.DefinitionClient.ListWorkflowsAsync(new ListWorkflowsRequest());
        Assert.DoesNotContain(list.Workflows, w => w.Reference.Name == "def-unload-all-test");
    }

    [Fact]
    public async Task UnloadWorkflow_ReturnsZero_WhenNothingMatched()
    {
        var response = await fixture.DefinitionClient.UnloadWorkflowAsync(new UnloadWorkflowRequest
        {
            Reference = new WorkflowReference { Name = "def-absent-test" }
        });

        Assert.Equal(0, response.Removed);
    }

    [Fact]
    public async Task ScheduleExecution_ThrowsNotFound_WhenDefinitionNotLoaded()
    {
        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.DaemonClient.ScheduleExecutionAsync(new ScheduleExecutionRequest
            {
                Workflow = new WorkflowReference { Name = "def-unscheduled-test" },
                StartActivity = "valid-activity"
            }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task UnloadWorkflow_DoesNotAffectExecutionsAlreadyScheduled()
    {
        // Arrange - schedule a run, then unload the definition it came from
        var request = new TestWorkflowBuilder("def-unload-safe-test", fixture.MockBaseUrl)
            .HttpGet("valid-activity", "/success")
            .Build();
        var scheduled = await fixture.ScheduleAsync(request);

        // Unloaded without waiting, so the definition goes away while the run is still in
        // flight — that is the case worth guarding.
        await fixture.DefinitionClient.UnloadWorkflowAsync(new UnloadWorkflowRequest
        {
            Reference = new WorkflowReference { Name = "def-unload-safe-test" }
        });

        // Act - the run finishes despite its definition having been removed mid-flight.
        await fixture.WatchToCompletionAsync(scheduled.Id);

        var details = await fixture.DaemonClient.GetExecutionAsync(
            new GetExecutionRequest { ExecutionId = scheduled.Id });

        // Assert - the execution snapshotted its own bundle at schedule time
        Assert.Equal(ExecutionStatus.Completed, details.Summary.Status);
        Assert.Equal(scheduled.Id, details.Summary.Id);
        Assert.NotNull(details.Bundle);
        Assert.True(details.Bundle.Workflows[details.Bundle.EntryPoint]
            .Spec.Activities.ContainsKey("valid-activity"));
    }
}
