using Common.Workflow;
using Common.Workflow.Proto;
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
        string databaseName)
    {
        builder.AddExecutionInfrastructure(databaseName);

        // The spec's duration grammar, supplied from here because this is the layer that may see
        // `Utos.Workflow`. The interpreter needs it to enter a timer whose duration is a template,
        // and reimplementing the grammar behind the boundary would be a second set of edge cases —
        // the first workflow to find the difference would be one the CLI accepted and this daemon
        // refused.
        builder.Services.AddSingleton<IDurationParser, SpecDurationParser>();

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
