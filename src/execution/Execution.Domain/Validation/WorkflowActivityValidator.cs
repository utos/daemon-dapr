using FluentValidation;
using Utos.Workflow.V1;

namespace Execution.Domain.Validation;

public class WorkflowActivityValidator : AbstractValidator<WorkflowActivity>
{
    public WorkflowActivityValidator()
    {
        // Validate HTTP activities
        When(a => a.ConfigCase == WorkflowActivity.ConfigOneofCase.Http, () =>
        {
            RuleFor(a => a.Http.Url)
                .NotEmpty()
                .WithMessage("HTTP activity URL cannot be empty")
                .Must(IsValidHttpUrl)
                .WithMessage(activity => GetHttpUrlError(activity.Http.Url));
        });

        // Validate timer activities. There is no maximum — a long wait is legitimate, and
        // Dapr splits long timers internally — but an unset or non-positive duration is
        // malformed and would otherwise only surface mid-run.
        When(a => a.ConfigCase == WorkflowActivity.ConfigOneofCase.Timer, () =>
        {
            RuleFor(a => a.Timer.Duration)
                .NotNull()
                .WithMessage("Timer activity requires a duration");

            RuleFor(a => a.Timer.Duration)
                .Must(duration => duration.ToTimeSpan() > TimeSpan.Zero)
                .When(a => a.Timer.Duration != null)
                .WithMessage("Timer duration must be positive");
        });

        // Validate promise activities
        When(a => a.ConfigCase == WorkflowActivity.ConfigOneofCase.Promise, () =>
        {
            RuleFor(a => a.Promise.Mode)
                .NotEmpty()
                .Must(m => m is "all" or "any" or "race" or "count")
                .WithMessage("Promise mode must be 'all', 'any', 'race', or 'count'");

            When(a => a.Promise.Mode == "count", () =>
            {
                RuleFor(a => a.Promise.RequiredCount)
                    .GreaterThan(0)
                    .WithMessage("Promise count mode requires required_count > 0");
            });

            RuleFor(a => a.Promise.Branches)
                .NotEmpty()
                .WithMessage("Promise activity must have at least one branch");

            RuleForEach(a => a.Promise.Branches).ChildRules(branch =>
            {
                branch.RuleFor(b => b.Name).NotEmpty().WithMessage("Branch name is required");
                branch.RuleFor(b => b.Target).NotNull().WithMessage("Branch target is required");
                branch.RuleFor(b => b.Target.Name).NotEmpty().WithMessage("Branch target name is required");
            });
        });

        // Validate transitions
        RuleForEach(a => a.OnSuccess)
            .SetValidator(new TransitionRuleValidator());
    }

    // Shape rules live in HttpUrlRules so the same checks run again on the rendered URL
    // at execution time — see ResolveActivityInputActivity.
    private static bool IsValidHttpUrl(string url) => HttpUrlRules.IsValidAuthored(url);

    private static string GetHttpUrlError(string url) => HttpUrlRules.GetError(url);
}
