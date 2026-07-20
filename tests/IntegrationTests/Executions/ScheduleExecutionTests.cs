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
    [InlineData("123invalid", "Activity name cannot start with number")]
    [InlineData("-invalid", "Activity name cannot start with dash")]
    [InlineData("_invalid", "Activity name cannot start with underscore")]
    [InlineData("invalid-", "Activity name cannot end with dash")]
    [InlineData("invalid_", "Activity name cannot end with underscore")]
    [InlineData("", "Activity name cannot be empty")]
    [InlineData("   ", "Activity name cannot be empty")]
    [InlineData("\t\n ", "Activity name cannot be empty")]
    [InlineData(" valid-name", "Activity name cannot start with whitespace")]
    [InlineData("valid-name ", "Activity name cannot end with whitespace")]
    public async Task WithInvalidActivityNameFormat_ShouldFail(string invalidName, string reason)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet(invalidName, "/success")
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains(reason.ToLowerInvariant(), exception.Status.Detail.ToLowerInvariant());
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
        Assert.Contains("activity name too long", exception.Status.Detail.ToLowerInvariant());
        Assert.Contains("maximum 63 characters", exception.Status.Detail.ToLowerInvariant());
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
        Assert.Contains("workflow must contain at least one activity", exception.Status.Detail.ToLowerInvariant());
    }

    [Theory]
    [InlineData("end")]
    [InlineData("error")]
    [InlineData("End")]
    [InlineData("ERROR")]
    public async Task WithReservedKeywordAsActivityName_ShouldFail(string reservedName)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet(reservedName, "/success")
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains("reserved keyword", exception.Status.Detail.ToLowerInvariant());
    }

    // ============================================================================
    // METADATA VALIDATION
    // ============================================================================

    [Theory]
    [InlineData("not-semantic-version", "Invalid semantic version format")]
    [InlineData("1.0", "Incomplete semantic version")]
    [InlineData("v1.0.0", "Semantic version should not include 'v' prefix")]
    [InlineData("", "Metadata version cannot be empty")]
    public async Task WithInvalidMetadataVersion_ShouldFail(string invalidVersion, string reason)
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
        Assert.Contains(reason.ToLowerInvariant(), exception.Status.Detail.ToLowerInvariant());
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
        Assert.Contains("description too long", exception.Status.Detail.ToLowerInvariant());
        Assert.Contains("maximum 500 characters", exception.Status.Detail.ToLowerInvariant());
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
    [InlineData("not-a-url", "Invalid URL format")]
    [InlineData("", "HTTP activity URL cannot be empty")]
    [InlineData("ftp://invalid-scheme.com", "Unsupported URL scheme")]
    [InlineData("javascript:alert('xss')", "Unsupported URL scheme")]
    public async Task WithInvalidHttpUrl_ShouldFail(string invalidUrl, string reason)
    {
        // Arrange
        var request = new TestWorkflowBuilder("test-workflow", fixture.MockBaseUrl)
            .HttpGet("test-activity", "/ignored", invalidUrl)
            .Build();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.ScheduleAsync(request));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Contains(reason.ToLowerInvariant(), exception.Status.Detail.ToLowerInvariant());
    }
}
