using Common.ExecutionLifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Observability.Application.Listeners;
using Observability.Application.Ports;
using Observability.Domain;
using Observability.Infrastructure.Logging;
using Observability.Infrastructure.Store;

namespace Observability.Infrastructure.Extensions;

/// <summary>
/// The log pipeline behind the watch stream: the provider that intercepts execution-scoped log
/// calls, the buffer they land in, and the listener that closes a buffer when its execution ends.
/// The gRPC endpoint is added by <c>Observability.Api</c>, which composes this.
/// </summary>
public static class ObservabilityInfrastructureExtensions
{
    public static IHostApplicationBuilder AddObservabilityInfrastructure(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IExecutionLogStore, ExecutionLogStore>();
        builder.Services.AddSingleton<ILoggerProvider, ExecutionLogProvider>();

        // Scoped, because whoever announces a finished run resolves listeners from a scope of
        // their own. Nothing here needs one today; taking it costs nothing and keeps the next
        // listener that does from being a captive dependency.
        builder.Services.AddScoped<IExecutionLifecycleListener, ExecutionLogStreamCompletionListener>();
        builder.Services.AddScoped<IExecutionProbe, EventStoreExecutionProbe>();

        return builder;
    }
}
