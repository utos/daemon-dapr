using FluentValidation;

namespace Common.Workflow.Validation;

public class TransitionRuleValidator : AbstractValidator<TransitionRule>
{
    public TransitionRuleValidator()
    {
        RuleFor(t => t.HasAction)
            .Equal(true)
            .WithMessage("Transition rule must have an action (transition or result)");

        When(t => t.Transition != null, () =>
        {
            RuleFor(t => t.Transition!)
                .SetValidator(new TransitionTargetValidator());
        });
    }
}

public class TransitionTargetValidator : AbstractValidator<TransitionTarget>
{
    public TransitionTargetValidator()
    {
        RuleFor(t => t.Name)
            .NotEmpty()
            .WithMessage("Transition target name cannot be empty");

        // Note: We don't validate if the target activity exists here
        // That validation happens at the workflow level where we have access to all activities
    }
}
