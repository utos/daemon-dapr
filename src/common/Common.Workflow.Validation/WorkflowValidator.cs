using FluentValidation;

namespace Common.Workflow.Validation;

public class WorkflowValidator : AbstractValidator<Workflow>
{
    public WorkflowValidator()
    {
        // Metadata validation
        RuleFor(w => w.Metadata)
            .SetValidator(new WorkflowMetadataValidator());

        // Activities collection validation
        RuleFor(w => w.Spec.Activities)
            .NotEmpty()
            .WithMessage("Workflow must contain at least one activity");

        // Individual activity validation
        RuleForEach(w => w.Spec.Activities)
            .ChildRules(activity =>
            {
                activity.RuleFor(kvp => kvp.Key)
                    .NotEmpty()
                    .WithMessage("Activity name cannot be empty")
                    .Must(name => !string.IsNullOrWhiteSpace(name))
                    .WithMessage("Activity name cannot be empty")
                    .Must(name => !ReservedKeywords.IsReserved(name))
                    // Inside ChildRules the message lambda is handed the entry, not the key —
                    // taking it as `name` would print the whole activity record back at the user.
                    .WithMessage(entry => $"Activity name '{entry.Key}' is a reserved keyword and cannot be used")
                    .Must(name => name.Length > 0 && !char.IsWhiteSpace(name[0]))
                    .WithMessage("Activity name cannot start with whitespace")
                    .Must(name => name.Length > 0 && !char.IsWhiteSpace(name[^1]))
                    .WithMessage("Activity name cannot end with whitespace")
                    .MaximumLength(63)
                    .WithMessage("Activity name too long. Maximum 63 characters allowed")
                    .Must(name => name.Length > 0 && !char.IsDigit(name[0]))
                    .WithMessage("Activity name cannot start with number")
                    .Must(name => name.Length > 0 && name[0] != '-')
                    .WithMessage("Activity name cannot start with dash")
                    .Must(name => name.Length > 0 && name[0] != '_')
                    .WithMessage("Activity name cannot start with underscore")
                    .Must(name => name.Length > 0 && name[^1] != '-')
                    .WithMessage("Activity name cannot end with dash")
                    .Must(name => name.Length > 0 && name[^1] != '_')
                    .WithMessage("Activity name cannot end with underscore")
                    .Matches("^[a-zA-Z0-9_-]+$")
                    .WithMessage("Activity name must contain only alphanumeric characters, underscores, and hyphens");

                activity.RuleFor(kvp => kvp.Value)
                    .SetValidator(new WorkflowActivityValidator());
            });

        // Validate transition targets reference existing activities or reserved keywords
        RuleFor(w => w)
            .Must(HaveValidTransitionTargets)
            .WithMessage(w => GetInvalidTransitionTargetsMessage(w));
    }

    private static bool HaveValidTransitionTargets(Workflow workflow)
    {
        var activityNames = workflow.Spec.Activities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var activity in workflow.Spec.Activities.Values)
        foreach (var rule in activity.OnSuccess)
        {
            if (rule.Transition is null)
                continue;

            var targetName = rule.Transition.Name;

            // Check if target is a reserved keyword or an existing activity
            if (!ReservedKeywords.IsReserved(targetName) && !activityNames.Contains(targetName)) return false;
        }

        return true;
    }

    private static string GetInvalidTransitionTargetsMessage(Workflow workflow)
    {
        var activityNames = workflow.Spec.Activities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidTargets = new List<string>();

        foreach (var (activityName, activity) in workflow.Spec.Activities)
        foreach (var rule in activity.OnSuccess)
        {
            if (rule.Transition is null)
                continue;

            var targetName = rule.Transition.Name;

            if (!ReservedKeywords.IsReserved(targetName) && !activityNames.Contains(targetName))
                invalidTargets.Add($"Activity '{activityName}' has invalid transition target '{targetName}'");
        }

        return string.Join("; ", invalidTargets);
    }
}
