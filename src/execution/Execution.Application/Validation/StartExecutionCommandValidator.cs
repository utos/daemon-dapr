using Execution.Application.Commands;
using FluentValidation;

namespace Execution.Application.Validation;

/// <summary>
/// What scheduling means beyond the bundle it names.
/// <para>
/// The bundle's own shape is not re-checked here. It was validated against the spec when it was
/// loaded, and by this point it comes from the definition store rather than from the caller, so
/// re-running the document rules on every schedule would only re-answer a settled question.
/// </para>
/// </summary>
public class StartExecutionCommandValidator : AbstractValidator<StartExecutionCommand>
{
    public StartExecutionCommandValidator()
    {
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
