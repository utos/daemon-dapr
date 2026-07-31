using Execution.Api.Services;
using Execution.Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Execution.Api.Extensions;

public static class ExecutionExtensions
{
    public static IHostApplicationBuilder AddExecutionModule(this IHostApplicationBuilder builder,
        string databaseName,
        string eventStoreName)
    {
        builder.AddExecutionInfrastructure(databaseName, eventStoreName);
        builder.Services.AddScoped<ExecutionGrpcService>();

        return builder;
    }

    public static WebApplication UseExecutionModule(this WebApplication app)
    {
        app.MapGrpcService<ExecutionGrpcService>();
        app.UseExecutionInfrastructure();

        return app;
    }
}
