using Common.Workflow.Validation;
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

        // The document's shape rules are shared; asking for them here is what keeps this module
        // standing on its own instead of on whichever module happened to register them.
        builder.Services.AddWorkflowValidation();
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
