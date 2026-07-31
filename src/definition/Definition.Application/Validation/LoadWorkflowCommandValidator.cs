using Common.Workflow;
using Definition.Application.Commands;
using FluentValidation;

namespace Definition.Application.Validation;

/// <summary>
/// Loading a definition and starting an execution accept the same document, so both run the same
/// shape rules — here through the pipeline, exactly as
/// <c>Execution.Application.Validation.StartExecutionCommandValidator</c> does.
/// </summary>
public class LoadWorkflowCommandValidator : AbstractValidator<LoadWorkflowCommand>
{
    public LoadWorkflowCommandValidator(IValidator<WorkflowBundle> bundleValidator)
    {
        RuleFor(c => c.Bundle)
            .SetValidator(bundleValidator);
    }
}
