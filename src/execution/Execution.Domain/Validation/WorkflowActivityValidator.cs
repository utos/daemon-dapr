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

    /// <summary>
    /// A URL whose scheme or host comes from a template — typically
    /// "{{ env.baseUrl }}/path" — is not an absolute URI until it is rendered, so its
    /// shape can only be checked at execution time.
    /// </summary>
    private static bool HasTemplateExpression(string url) => url.Contains("{{");

    private static bool IsValidHttpUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (HasTemplateExpression(url))
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == "http" || uri.Scheme == "https";
    }

    private static string GetHttpUrlError(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "HTTP activity URL cannot be empty";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Invalid URL format";

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return "Unsupported URL scheme (only HTTP/HTTPS allowed)";

        return "Invalid URL format";
    }
}
