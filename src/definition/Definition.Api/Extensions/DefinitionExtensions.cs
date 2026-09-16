using Definition.Api.Services;
using Definition.Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Definition.Api.Extensions;

public static class DefinitionExtensions
{
    public static IHostApplicationBuilder AddDefinitionModule(this IHostApplicationBuilder builder)
    {
        builder.Services.AddDefinitionInfrastructure();

        // No bundle validator is registered: the document's shape rules are the spec's, and
        // DefinitionGrpcService applies them to the wire form before it becomes a command. What
        // is left for the pipeline is whatever a command means beyond its bundle.
        builder.Services.AddScoped<DefinitionGrpcService>();

        return builder;
    }

    public static WebApplication UseDefinitionModule(this WebApplication app)
    {
        app.MapGrpcService<DefinitionGrpcService>();
        return app;
    }
}
