using Common.Workflow;
using FluentValidation;

namespace Execution.Domain.Validation;

public class WorkflowBundleValidator : AbstractValidator<WorkflowBundle>
{
    public WorkflowBundleValidator()
    {
        RuleFor(b => b.EntryPoint)
            .NotEmpty()
            .WithMessage("Bundle entry_point is required");

        RuleFor(b => b.Workflows)
            .NotEmpty()
            .WithMessage("Bundle must contain at least one workflow");

        RuleFor(b => b)
            .Must(b => b.Workflows.ContainsKey(b.EntryPoint))
            .When(b => !string.IsNullOrEmpty(b.EntryPoint) && b.Workflows.Count > 0)
            .WithMessage(b =>
                $"Entry point '{b.EntryPoint}' not found in bundle workflows. Available: {string.Join(", ", b.Workflows.Keys)}");

        RuleForEach(b => b.Workflows)
            .ChildRules(workflow =>
            {
                workflow.RuleFor(kvp => kvp.Key)
                    .NotEmpty()
                    .WithMessage("Workflow name cannot be empty");

                workflow.RuleFor(kvp => kvp.Value)
                    .SetValidator(new WorkflowValidator());
            });
    }
}
