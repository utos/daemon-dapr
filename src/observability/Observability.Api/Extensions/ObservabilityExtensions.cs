using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Observability.Api.Services;
using Observability.Infrastructure.Extensions;

namespace Observability.Api.Extensions;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddObservabilityModule(this IHostApplicationBuilder builder)
    {
        builder.AddObservabilityInfrastructure();
        builder.Services.AddScoped<ObservabilityGrpcService>();

        return builder;
    }

    public static WebApplication UseObservabilityModule(this WebApplication app)
    {
        app.MapGrpcService<ObservabilityGrpcService>();
        return app;
    }
}
