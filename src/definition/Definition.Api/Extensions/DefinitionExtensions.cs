using Common.Workflow.Validation;
using Definition.Api.Services;
using Definition.Application.Commands;
using Definition.Application.Validation;
using Definition.Domain;
using Definition.Infrastructure.Store;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Definition.Api.Extensions;

public static class DefinitionExtensions
{
    public static IHostApplicationBuilder AddDefinitionModule(this IHostApplicationBuilder builder)
    {
        // Singleton: the store is the daemon's process-wide definition registry.
        builder.Services.AddSingleton<IWorkflowDefinitionStore, InMemoryWorkflowDefinitionStore>();

        // A bundle arrives here before it ever reaches an execution, so this module runs the
        // document's shape rules itself rather than borrowing another module's registration.
        builder.Services.AddWorkflowValidation();
        builder.Services.AddScoped<IValidator<LoadWorkflowCommand>, LoadWorkflowCommandValidator>();

        builder.Services.AddScoped<DefinitionGrpcService>();

        return builder;
    }

    public static WebApplication UseDefinitionModule(this WebApplication app)
    {
        app.MapGrpcService<DefinitionGrpcService>();
        return app;
    }
}
