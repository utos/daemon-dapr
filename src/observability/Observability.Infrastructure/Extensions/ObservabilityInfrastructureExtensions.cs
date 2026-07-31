using Common.Infrastructure.EventHandling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Observability.Application.Ports;
using Observability.Domain;
using Observability.Infrastructure.EventHandling;
using Observability.Infrastructure.Logging;
using Observability.Infrastructure.Store;

namespace Observability.Infrastructure.Extensions;

/// <summary>
/// The log pipeline behind the watch stream: the provider that intercepts execution-scoped log
/// calls, the buffer they land in, and the handler that closes a buffer when its execution ends.
/// The gRPC endpoint is added by <c>Observability.Api</c>, which composes this.
/// </summary>
public static class ObservabilityInfrastructureExtensions
{
    public static IHostApplicationBuilder AddObservabilityInfrastructure(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IExecutionLogStore, ExecutionLogStore>();
        builder.Services.AddSingleton<ILoggerProvider, ExecutionLogProvider>();
        builder.Services.AddSingleton<IEventHandler, ExecutionLogStreamCompletionHandler>();
        builder.Services.AddScoped<IExecutionProbe, EventStoreExecutionProbe>();

        return builder;
    }
}
