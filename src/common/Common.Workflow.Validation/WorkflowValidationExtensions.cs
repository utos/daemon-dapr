using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Common.Workflow.Validation;

public static class WorkflowValidationExtensions
{
    /// <summary>
    /// Registers the workflow document's shape rules. Every module that accepts a bundle from a
    /// client calls this: the rules are shared, but the registration is not — a module that needs
    /// them says so itself rather than relying on another module having been added first.
    /// </summary>
    public static IServiceCollection AddWorkflowValidation(this IServiceCollection services)
    {
        // The bundle validator is the only one anyone injects; everything below it is composed
        // with `new` inside its parent, so registering those buys nothing.
        services.TryAddScoped<IValidator<WorkflowBundle>, WorkflowBundleValidator>();

        return services;
    }
}
