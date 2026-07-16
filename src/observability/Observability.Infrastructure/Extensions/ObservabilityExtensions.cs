using Common.Infrastructure.EventHandling;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Observability.Domain;
using Observability.Features.Services;
using Observability.Infrastructure.EventHandling;
using Observability.Infrastructure.Logging;
using Observability.Infrastructure.Store;

namespace Observability.Infrastructure.Extensions;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddObservabilityModule(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IExecutionLogStore, ExecutionLogStore>();
        builder.Services.AddSingleton<ILoggerProvider, ExecutionLogProvider>();
        builder.Services.AddScoped<ObservabilityGrpcService>();
        builder.Services.AddSingleton<IEventHandler, ExecutionLogStreamCompletionHandler>();

        return builder;
    }

    public static WebApplication UseObservabilityModule(this WebApplication app)
    {
        app.MapGrpcService<ObservabilityGrpcService>();
        return app;
    }
}
