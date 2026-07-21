using Common.Infrastructure.Data;
using Common.Infrastructure.EventHandling;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Projections;
using Common.Infrastructure.Serialization;
using Common.SeedWork;
using Dapr.Workflow;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Utos.Workflow.V1;
using Execution.Features.Repositories;
using Execution.Domain.Validation;
using Execution.Features.Behaviors;
using Execution.Features.Commands;
using Execution.Features.Models;
using Execution.Features.Services;
using Execution.Features.Validation;
using Mediator;
using Execution.Domain.Services;
using Execution.Infrastructure.Context;
using Execution.Infrastructure.Dapr;
using Execution.Infrastructure.Dapr.Activities;
using Execution.Infrastructure.Dapr.ActivityHandlers;
using Execution.Infrastructure.Data;
using Execution.Infrastructure.EventProcessing;
using Execution.Infrastructure.Persistence;
using Execution.Infrastructure.EventHandling;
using Execution.Infrastructure.Projections;
using Execution.Infrastructure.Repositories;
using Execution.Infrastructure.Templating;

namespace Execution.Infrastructure.Extensions;

public static class ExecutionExtensions
{
    public static IHostApplicationBuilder AddExecutionModule(this IHostApplicationBuilder builder,
        string databaseName,
        string eventStoreName)
    {
        // Configure System.Text.Json globally for Dapr and other serialization
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new ObjectDictionaryJsonConverter());
        });

        var database = builder.Configuration.GetConnectionString(databaseName);

        // Add Db Context & Auditable Entity Interceptor
        builder.Services.AddDbContext<WorkflowExecutionDbContext>(options =>
        {
            options.UseSqlServer(database);
            options.AddInterceptors(new AuditableEntityInterceptor());
        });

        // Add KurrentDBClient
        builder.AddKurrentDBClient(eventStoreName);

        // Add KurrentDB Persistent Subscriptions Client
        builder.Services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connectionString = config.GetConnectionString(eventStoreName);
            return new KurrentDB.Client.KurrentDBPersistentSubscriptionsClient(
                KurrentDB.Client.KurrentDBClientSettings.Create(connectionString!));
        });

        // Add EventStore Serializer
        builder.Services.AddSingleton<IEventSerializer, EventSerializer>();

        // Add repositories
        builder.Services.AddScoped<IEventStoreRepository, EventStoreRepository>();
        builder.Services.AddScoped<IWorkflowExecutionRepository, WorkflowExecutionRepository>();

        // Add Templating engine
        builder.Services.AddSingleton<ITemplateEngine, ScribanTemplateEngine>();

        // Add Execution Context Store
        builder.Services.AddScoped<IExecutionContextStore, EfCoreExecutionContextStore>();

        // Add Activity Handlers (strategy pattern for activity type dispatch)
        builder.Services.AddScoped<IActivityHandler, HttpActivityHandler>();
        builder.Services.AddHttpClient<HttpActivityHandler>();

        // Add Dapr Workflow services
        builder.Services.AddDaprWorkflow(options =>
        {
            // Register workflow executor
            options.RegisterWorkflow<WorkflowExecutor>();

            // Register activities
            options.RegisterActivity<WriteLifecycleEventActivity>();
            options.RegisterActivity<LoadWorkflowActivity>();
            options.RegisterActivity<ResolveActivityInputActivity>();
            options.RegisterActivity<ExecuteActivity>();
            options.RegisterActivity<EvaluateTransitionActivity>();
            options.RegisterActivity<SaveChildContextActivity>();
            options.RegisterActivity<ResolvePromiseActivity>();
        });

        // Projection engine and projections (idempotent read model updates)
        builder.Services.AddScoped<IProjectionEngine, ProjectionEngine>();
        builder.Services.AddScoped<IProjection, WorkflowExecutionReadModelProjection>();
        builder.Services.AddScoped<IProjection, SubWorkflowCallProjection>();

        // Event handlers (side-effect-producing reactions)
        builder.Services.AddScoped<IEventHandler, WorkflowSchedulingProcessManager>();

        // Execution limits (loop ceiling, continue-as-new threshold)
        builder.Services.Configure<ExecutionLimitsOptions>(
            builder.Configuration.GetSection(ExecutionLimitsOptions.SectionName));

        // Add validation
        builder.Services.AddScoped<IValidator<WorkflowBundle>, WorkflowBundleValidator>();
        builder.Services.AddScoped<IValidator<Workflow>, WorkflowValidator>();
        builder.Services.AddScoped<IValidator<ScheduleExecutionCommand>, ScheduleExecutionCommandValidator>();
        builder.Services.AddScoped<WorkflowActivityValidator>();
        builder.Services.AddScoped<WorkflowMetadataValidator>();

        // Add Mediator (source-generated)
        builder.Services.AddMediator(options => { options.ServiceLifetime = ServiceLifetime.Scoped; });
        builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // Add gRPC services
        builder.Services.AddGrpc();
        builder.Services.AddScoped<ExecutionGrpcService>();

        // Add Hosted Services
        builder.Services.AddHostedService<WorkflowExecutionEventProcessor>();

        return builder;
    }

    public static WebApplication UseExecutionModule(this WebApplication app)
    {
        // Map gRPC service endpoints
        app.MapGrpcService<ExecutionGrpcService>();

        // Auto-apply migrations on startup (since database is ephemeral)
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowExecutionDbContext>();
            dbContext.Database.Migrate();
        }

        return app;
    }
}
