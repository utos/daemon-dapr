using FluentValidation;

namespace Common.Workflow.Validation;

public class WorkflowActivityValidator : AbstractValidator<WorkflowActivity>
{
    public WorkflowActivityValidator()
    {
        // The config is a closed hierarchy, so each kind gets its own validator rather than
        // one validator reaching through a discriminator.
        RuleFor(a => a.Config)
            .SetInheritanceValidator(v =>
            {
                v.Add(new HttpActivityConfigValidator());
                v.Add(new TimerActivityConfigValidator());
                v.Add(new PromiseActivityConfigValidator());
            });

        // Validate transitions
        RuleForEach(a => a.OnSuccess)
            .SetValidator(new TransitionRuleValidator());
    }
}

public class HttpActivityConfigValidator : AbstractValidator<HttpActivityConfig>
{
    public HttpActivityConfigValidator()
    {
        RuleFor(c => c.Url)
            .NotEmpty()
            .WithMessage("HTTP activity URL cannot be empty")
            // Shape rules live in HttpUrlRules so the same checks run again on the rendered
            // URL at execution time — see WorkflowInterpreter.ResolveHttp.
            .Must(HttpUrlRules.IsValidAuthored)
            .WithMessage(config => HttpUrlRules.GetError(config.Url));
    }
}

public class TimerActivityConfigValidator : AbstractValidator<TimerActivityConfig>
{
    public TimerActivityConfigValidator()
    {
        // There is no maximum — a long wait is legitimate, and Dapr splits long timers
        // internally — but an unset or non-positive duration is malformed and would otherwise
        // only surface mid-run.
        RuleFor(c => c.Duration)
            .NotNull()
            .WithMessage("Timer activity requires a duration");

        RuleFor(c => c.Duration)
            .Must(duration => duration > TimeSpan.Zero)
            .When(c => c.Duration.HasValue)
            .WithMessage("Timer duration must be positive");
    }
}

public class PromiseActivityConfigValidator : AbstractValidator<PromiseActivityConfig>
{
    public PromiseActivityConfigValidator()
    {
        RuleFor(c => c.Mode)
            .NotEmpty()
            .Must(PromiseModes.IsKnown)
            .WithMessage("Promise mode must be 'all', 'any', 'race', or 'count'");

        When(c => c.Mode == PromiseModes.Count, () =>
        {
            RuleFor(c => c.RequiredCount)
                .GreaterThan(0)
                .WithMessage("Promise count mode requires required_count > 0");
        });

        RuleFor(c => c.Branches)
            .NotEmpty()
            .WithMessage("Promise activity must have at least one branch");

        RuleForEach(c => c.Branches).ChildRules(branch =>
        {
            branch.RuleFor(b => b.Name).NotEmpty().WithMessage("Branch name is required");
            branch.RuleFor(b => b.Target).NotNull().WithMessage("Branch target is required");
            branch.RuleFor(b => b.Target.Name).NotEmpty().WithMessage("Branch target name is required");
        });
    }
}
