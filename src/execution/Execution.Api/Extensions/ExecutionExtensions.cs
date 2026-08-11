using Execution.Api.Services;
using Execution.Application.Commands;
using Execution.Application.Validation;
using Execution.Infrastructure.Extensions;
using FluentValidation;
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

        // Only what scheduling means beyond the bundle. The bundle's own shape was validated
        // against the spec when it was loaded, and it has not changed since — it comes from the
        // definition store, not from the caller.
        builder.Services.AddScoped<IValidator<StartExecutionCommand>, StartExecutionCommandValidator>();

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
