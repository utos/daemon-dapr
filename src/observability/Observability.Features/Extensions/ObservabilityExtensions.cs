using Common.SeedWork;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Observability.Domain;
using Observability.Features.Persistence;
using Observability.Features.Services;

namespace Observability.Features.Extensions;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddObservabilityModule(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<ObservabilityGrpcService>();

        // Add Workflow Logger (cross-cutting capability)
        builder.Services.AddScoped<IWorkflowLogger, WorkflowLogger>();

        // Add Log Stream Repository
        builder.Services.AddScoped<ILogStreamRepository, LogStreamRepository>();

        return builder;
    }

    public static WebApplication UseObservabilityModule(this WebApplication app)
    {
        app.MapGrpcService<ObservabilityGrpcService>();
        return app;
    }
}
