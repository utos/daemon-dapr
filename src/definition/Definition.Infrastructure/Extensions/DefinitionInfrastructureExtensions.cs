using Common.Workflow.Resolution;
using Definition.Domain;
using Definition.Infrastructure.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Definition.Infrastructure.Extensions;

/// <summary>
/// The definition catalog, and the one question other contexts may ask it. The gRPC endpoint and
/// the document's shape rules are added by <c>Definition.Api</c>, which composes this — the same
/// split the other two modules use.
/// </summary>
public static class DefinitionInfrastructureExtensions
{
    public static IServiceCollection AddDefinitionInfrastructure(this IServiceCollection services)
    {
        // Singleton: the store is the daemon's process-wide definition registry.
        services.AddSingleton<IWorkflowDefinitionStore, InMemoryWorkflowDefinitionStore>();

        // Registered here rather than beside the consumer: the module that owns the definitions
        // is the one that decides what it will answer about them.
        services.AddSingleton<IWorkflowResolver, WorkflowDefinitionResolver>();

        return services;
    }
}
