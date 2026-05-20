namespace Execution.Infrastructure.ReadModels;

public class SubWorkflowCall
{
    public Guid ParentExecutionId { get; set; }
    public Guid ChildExecutionId { get; set; }
    public string ActivityName { get; set; } = null!;
    public string InvocationType { get; set; } = null!;
    public string? PromiseMode { get; set; }
    public string? BranchName { get; set; }
}
