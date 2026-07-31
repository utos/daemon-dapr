using Common.Workflow;
using Execution.Application.Commands;
using FluentValidation;

namespace Execution.Application.Validation;

public class StartExecutionCommandValidator : AbstractValidator<StartExecutionCommand>
{
    public StartExecutionCommandValidator(IValidator<WorkflowBundle> bundleValidator)
    {
        RuleFor(c => c.Bundle)
            .SetValidator(bundleValidator);

        RuleFor(c => c.StartActivity)
            .NotEmpty()
            .WithMessage("start_activity is required");

        RuleFor(c => c)
            .Must(c => c.Bundle.Workflows.TryGetValue(c.Bundle.EntryPoint, out var wf)
                       && wf.Spec.Activities.ContainsKey(c.StartActivity))
            .When(c => !string.IsNullOrEmpty(c.StartActivity)
                       && !string.IsNullOrEmpty(c.Bundle.EntryPoint)
                       && c.Bundle.Workflows.ContainsKey(c.Bundle.EntryPoint))
            .WithMessage(c =>
            {
                if (c.Bundle.Workflows.TryGetValue(c.Bundle.EntryPoint, out var wf))
                {
                    var available = string.Join(", ", wf.Spec.Activities.Keys);
                    return
                        $"start_activity '{c.StartActivity}' not found in workflow. Available activities: {available}";
                }

                return $"start_activity '{c.StartActivity}' not found in workflow";
            });
    }
}
