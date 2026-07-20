using FluentValidation;
using Utos.Workflow.V1;

namespace Execution.Domain.Validation;

public class TransitionRuleValidator : AbstractValidator<TransitionRule>
{
    public TransitionRuleValidator()
    {
        RuleFor(t => t.ActionCase)
            .NotEqual(TransitionRule.ActionOneofCase.None)
            .WithMessage("Transition rule must have an action (transition or result)");

        When(t => t.ActionCase == TransitionRule.ActionOneofCase.Transition, () =>
        {
            RuleFor(t => t.Transition)
                .NotNull()
                .WithMessage("Transition target cannot be null")
                .SetValidator(new TransitionTargetValidator());
        });

        When(t => t.ActionCase == TransitionRule.ActionOneofCase.Result, () =>
        {
            RuleFor(t => t.Result)
                .NotNull()
                .WithMessage("Result value cannot be null");
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
