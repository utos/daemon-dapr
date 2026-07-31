using System.Text.RegularExpressions;
using Common.Workflow;
using FluentValidation;

namespace Execution.Domain.Validation;

public class WorkflowMetadataValidator : AbstractValidator<WorkflowMetadata>
{
    // Semantic version regex from schema
    private static readonly Regex SemanticVersionPattern =
        new(
            @"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$");

    // Workflow name pattern: lowercase, alphanumeric, hyphens allowed
    private static readonly Regex NamePattern = new(@"^[a-z0-9][a-z0-9-]*[a-z0-9]$|^[a-z0-9]$");

    public WorkflowMetadataValidator()
    {
        RuleFor(m => m.Name)
            .NotEmpty()
            .WithMessage("Workflow name cannot be empty")
            .MaximumLength(63)
            .WithMessage("Workflow name too long (maximum 63 characters)")
            .Must(name => NamePattern.IsMatch(name))
            .WithMessage(metadata => GetNameError(metadata.Name));

        RuleFor(m => m.Version)
            .NotEmpty()
            .WithMessage("Metadata version cannot be empty")
            .Must(version => SemanticVersionPattern.IsMatch(version))
            .WithMessage(metadata => GetVersionError(metadata.Version));

        RuleFor(m => m.Description)
            .MaximumLength(500)
            .WithMessage("Description too long (maximum 500 characters)");
    }

    private static string GetNameError(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Workflow name cannot be empty";

        if (name.Length > 63)
            return "Workflow name too long (maximum 63 characters)";

        if (!char.IsLower(name[0]) && !char.IsDigit(name[0]))
            return "Workflow name must start with lowercase letter or digit";

        if (name.Length > 1 && name[^1] == '-')
            return "Workflow name cannot end with hyphen";

        if (name.ToLowerInvariant() != name)
            return "Workflow name must be lowercase";

        if (name.Contains('_'))
            return "Workflow name cannot contain underscores (use hyphens instead)";

        return
            $"Workflow name '{name}' contains invalid characters (only lowercase letters, digits, and hyphens allowed)";
    }

    private static string GetVersionError(string version)
    {
        if (string.IsNullOrEmpty(version))
            return "Metadata version cannot be empty";

        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return "Semantic version should not include 'v' prefix";

        // Check for incomplete versions (has dots but not enough parts)
        var parts = version.Split('.');
        if (parts.Length >= 2 && parts.Length < 3)
            return "Incomplete semantic version (must be MAJOR.MINOR.PATCH)";

        return "Invalid semantic version format";
    }
}
