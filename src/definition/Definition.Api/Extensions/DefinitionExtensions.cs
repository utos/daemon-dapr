using Definition.Api.Services;
using Definition.Domain;
using Definition.Infrastructure.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Definition.Api.Extensions;

public static class DefinitionExtensions
{
    public static IHostApplicationBuilder AddDefinitionModule(this IHostApplicationBuilder builder)
    {
        // Singleton: the store is the daemon's process-wide definition registry.
        builder.Services.AddSingleton<IWorkflowDefinitionStore, InMemoryWorkflowDefinitionStore>();
        builder.Services.AddScoped<DefinitionGrpcService>();

        return builder;
    }

    public static WebApplication UseDefinitionModule(this WebApplication app)
    {
        app.MapGrpcService<DefinitionGrpcService>();
        return app;
    }
}
