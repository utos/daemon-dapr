using Common.Constants;
using Common.Workflow;
using Common.Workflow.Validation;
using UnitTests.Support;

namespace UnitTests.Validation;

/// <summary>
/// The rules a bundle must satisfy before either module accepts it. Both <c>LoadWorkflow</c> and
/// <c>ScheduleExecution</c> reach these through the validation pipeline, so the messages are what
/// a client sees as the <c>InvalidArgument</c> detail.
/// </summary>
public class WorkflowBundleValidatorTests
{
    private readonly WorkflowBundleValidator _validator = new();

    [Fact]
    public void ValidBundle_Passes()
    {
        var result = _validator.Validate(Bundle("test:1.0.0"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MissingEntryPoint_Fails()
    {
        var result = _validator.Validate(Bundle(string.Empty));

        Assert.Contains(result.Errors, e => e.ErrorMessage == "Bundle entry_point is required");
    }

    [Fact]
    public void NoWorkflows_Fails()
    {
        var bundle = new WorkflowBundle("test:1.0.0", new Dictionary<string, Workflow>());

        var result = _validator.Validate(bundle);

        Assert.Contains(result.Errors, e => e.ErrorMessage == "Bundle must contain at least one workflow");
    }

    [Fact]
    public void EntryPointNotInWorkflows_Fails()
    {
        var result = _validator.Validate(Bundle("absent:1.0.0"));

        Assert.Contains(result.Errors, e =>
            e.ErrorMessage.Contains("Entry point 'absent:1.0.0' not found in bundle workflows"));
    }

    /// <summary>The bundle validator carries the workflow's own rules down with it.</summary>
    [Fact]
    public void InvalidActivityInsideWorkflow_Fails()
    {
        var workflow = Workflow(("end", Wf.Http("https://example.test/run")));
        var bundle = new WorkflowBundle("test:1.0.0",
            new Dictionary<string, Workflow> { ["test:1.0.0"] = workflow });

        var result = _validator.Validate(bundle);

        Assert.Contains(result.Errors, e =>
            e.ErrorMessage == "Activity name 'end' is a reserved keyword and cannot be used");
    }

    private static WorkflowBundle Bundle(string entryPoint)
    {
        return new WorkflowBundle(entryPoint,
            new Dictionary<string, Workflow> { ["test:1.0.0"] = Workflow() });
    }

    private static Workflow Workflow(params (string Name, WorkflowActivity Activity)[] activities)
    {
        if (activities.Length == 0)
            activities = [("run", Wf.Http("https://example.test/run"))];

        return new Workflow(
            WorkflowDocument.ApiVersion,
            WorkflowDocument.Kind,
            new WorkflowMetadata("test", "1.0.0"),
            new WorkflowSpec(activities.ToDictionary(a => a.Name, a => a.Activity)));
    }
}
