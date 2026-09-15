using Grpc.Core;
using IntegrationTests.Shared.Builders;
using IntegrationTests.Shared.Fixtures;
using Utos.Daemon.V1;

namespace IntegrationTests.Executions;

/// <summary>
/// Tests for ScheduleExecution API endpoint input validation.
/// Focuses on validating request parameters, workflow structure, and business rules.
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class ScheduleExecutionValidationTests(DaemonTestFixture fixture)
{
    // ============================================================================
    // SUCCESS CASES
    // ============================================================================

    [Fact]
    public async Task WithValidWorkflow_ShouldSucceed()
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl,
                "1.0.0", "Basic test workflow")
            .HttpGet("test-activity", "/success")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response.Id);
        Assert.True(Guid.TryParse(response.Id, out _)); // Should be a valid GUID
        Assert.Equal(ExecutionStatus.Unspecified, response.Status);
    }

    [Fact]
    public async Task WithMultipleValidActivities_ShouldSucceed()
    {
        // Arrange
        var request = new TestWorkflowBuilder("multi-activity-workflow", fixture.MockBaseUrl)
            .HttpGet("greet-user", "/success")
            .HttpGet("validate-input", "/success")
            .HttpGet("process-payment-123", "/success")
            .HttpGet("send_confirmation_email", "/success")
            .BuildSequential();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response.Id);
        Assert.True(Guid.TryParse(response.Id, out _));
        Assert.Equal(ExecutionStatus.Unspecified, response.Status);
    }

    [Theory]
    [InlineData("a")] // Minimum length (1 char)
    [InlineData("process-payment-step-1-verify-card-details-and-validate-user")] // Long descriptive name (63 chars)
    [InlineData("API_Call_With_Underscores_123")] // Mixed case with underscores and numbers
    [InlineData("x1-y2_z3-a4_b5")] // Complex mix of valid characters
    [InlineData("emergency_response")] // Underscore separated
    [InlineData("step123")] // Ends with numbers
    public async Task WithValidComplexActivityName_ShouldSucceed(string activityName)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet(activityName, "/success")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response.Id);
        Assert.True(Guid.TryParse(response.Id, out _));
        Assert.Equal(ExecutionStatus.Unspecified, response.Status);
    }

    // ============================================================================
    // ACTIVITY NAME VALIDATION
    // ============================================================================

    [Theory]
    // These assert the rule code rather than the wording. api/docs/workflow-validation.md pins
    // code and path as the contract and deliberately leaves message text free to improve, so a
    // test that matched on prose would break every time an error was made clearer.
    [InlineData("123invalid", "UTOS-A005")]   // may not begin with a digit, hyphen or underscore
    [InlineData("-invalid", "UTOS-A005")]
    [InlineData("_invalid", "UTOS-A005")]
    [InlineData("invalid-", "UTOS-A006")]     // may not end with a hyphen or underscore
    [InlineData("invalid_", "UTOS-A006")]
    [InlineData("", "UTOS-A001")]             // required, and no surrounding whitespace
    [InlineData("   ", "UTOS-A001")]
    [InlineData("\t\n ", "UTOS-A001")]
    [InlineData(" valid-name", "UTOS-A001")]
    [InlineData("valid-name ", "UTOS-A001")]
    public async Task WithInvalidActivityNameFormat_ShouldFail(string invalidName, string expectedCode)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet(invalidName, "/success")
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains(expectedCode, exception.Status.Detail);
    }

    [Fact]
    public async Task WithTooLongActivityName_ShouldFail()
    {
        // Arrange - Create a 64-character activity name (1 char over limit)
        var tooLongName = "a" + new string('x', 62) + "z"; // a + 62 x's + z = 64 chars

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet(tooLongName, "/success")
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-A002", exception.Status.Detail);
    }

    // NOTE: WithDuplicateActivityNames_ShouldFail test removed
    // With map-based activities, duplicate keys are prevented by Dictionary<TKey, TValue>
    // No need to test framework behavior

    // ============================================================================
    // WORKFLOW STRUCTURE VALIDATION
    // ============================================================================

    [Fact]
    public async Task WithEmptyActivitiesList_ShouldFail()
    {
        // Arrange - Create workflow with no activities
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-D005", exception.Status.Detail);
    }

    [Theory]
    [InlineData("end")]
    [InlineData("error")]
    public async Task An_activity_may_be_named_after_a_former_keyword(string name)
    {
        // Until spec 0.0.15 these were reserved (UTOS-A003) so that a transition target was never
        // ambiguous. Ending and failing a path are actions now, nothing is reserved, and the rule
        // is retired — so this must load and run, not be refused.
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet(name, "/success")
            .Build();

        var response = await fixture.ScheduleAsync(request);

        Assert.False(string.IsNullOrEmpty(response.Id));
    }

    [Theory]
    [InlineData("end")]
    [InlineData("error")]
    public async Task A_transition_to_a_former_keyword_is_an_unresolved_target(string name)
    {
        // The other half of the same change: a document written for 0.0.15 that still
        // transitions to `end` or `error` fails at load, by name, rather than running differently.
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("call", "/success")
            .OnSuccess(name)
            .Build();

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-T003", exception.Status.Detail);
    }

    // ============================================================================
    // METADATA VALIDATION
    // ============================================================================

    [Theory]
    [InlineData("not-semantic-version", "UTOS-M005")]
    [InlineData("1.0", "UTOS-M005")]
    [InlineData("v1.0.0", "UTOS-M005")]   // the 'v' prefix is rejected
    [InlineData("", "UTOS-M004")]         // absent is a different rule from malformed
    public async Task WithInvalidMetadataVersion_ShouldFail(string invalidVersion, string expectedCode)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl,
                invalidVersion)
            .HttpGet("valid-activity", "/success")
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains(expectedCode, exception.Status.Detail);
    }

    [Fact]
    public async Task WithVeryLongDescription_ShouldSucceed()
    {
        // Arrange - 500 characters (at the limit)
        var longDescription = new string('x', 500);

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl,
                description: longDescription)
            .HttpGet("test-activity", "/success")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response.Id);
        Assert.Equal(ExecutionStatus.Unspecified, response.Status);
    }

    [Fact]
    public async Task WithTooLongDescription_ShouldFail()
    {
        // Arrange - 501 characters (over the limit)
        var tooLongDescription = new string('x', 501);

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl,
                description: tooLongDescription)
            .HttpGet("test-activity", "/success")
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("UTOS-M006", exception.Status.Detail);
    }

    [Fact]
    public async Task WithSpecialCharactersInDescription_ShouldSucceed()
    {
        // Arrange - Test various special characters and Unicode
        var specialDescription = "Workflow with special chars: !@#$%^&*()[]{}|\\:;\"'<>,.?/~`" +
                                 "Unicode: café, naïve, résumé, Ω, α, β, 中文, 日本語, العربية" +
                                 "Emojis: 🚀 🎯 ✅ 🔥 💡";

        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl,
                description: specialDescription)
            .HttpGet("test-activity", "/success")
            .Build();

        // Act
        var response = await fixture.ScheduleAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response.Id);
        Assert.Equal(ExecutionStatus.Unspecified, response.Status);
    }

    // ============================================================================
    // HTTP ACTIVITY VALIDATION
    // ============================================================================

    [Theory]
    [InlineData("not-a-url", "UTOS-C102")]
    [InlineData("", "UTOS-C101")]         // absent is a different rule from malformed
    [InlineData("ftp://invalid-scheme.com", "UTOS-C102")]
    [InlineData("javascript:alert('xss')", "UTOS-C102")]
    public async Task WithInvalidHttpUrl_ShouldFail(string invalidUrl, string expectedCode)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("test-activity", "/ignored", invalidUrl)
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains(expectedCode, exception.Status.Detail);
    }
}
