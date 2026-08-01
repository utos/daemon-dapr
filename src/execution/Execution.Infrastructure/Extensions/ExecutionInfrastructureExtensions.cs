using Common.Infrastructure.Data;
using Common.Infrastructure.EventHandling;
using Common.Infrastructure.EventStore;
using Common.Infrastructure.Projections;
using Common.Workflow.Serialization;
using Common.SeedWork;
using Dapr.Workflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Execution.Application.Models;
using Execution.Application.Repositories;
using Execution.Domain.Interpreter;
using Execution.Domain.Services;
using Execution.Domain.Templating;
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

/// <summary>
/// Everything the execution module needs to run, minus its service surface: persistence, the
/// Dapr driver, projections, and the application layer's own registrations. The gRPC endpoints
/// are added by <c>Execution.Api</c>, which composes this.
/// </summary>
public static class ExecutionInfrastructureExtensions
{
    public static IHostApplicationBuilder AddExecutionInfrastructure(this IHostApplicationBuilder builder,
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

        // The single path by which anything writes an execution's lifecycle
        builder.Services.AddScoped<ExecutionLifecycleWriter>();

        // Add Templating engine
        builder.Services.AddSingleton<ITemplateEngine, ScribanTemplateEngine>();

        // Add the workflow interpreter (pure decision logic, no Dapr/EF)
        builder.Services.AddSingleton<StructRenderer>();
        builder.Services.AddSingleton<WorkflowInterpreter>();

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
            options.RegisterActivity<ScheduleDetachedExecutionActivity>();
            options.RegisterActivity<ResolvePromiseActivity>();
        });

        // Projection engine and projections (idempotent read model updates)
        builder.Services.AddScoped<IProjectionEngine, ProjectionEngine>();
        builder.Services.AddScoped<IProjection, WorkflowExecutionReadModelProjection>();

        // Event handlers (side-effect-producing reactions)
        builder.Services.AddScoped<IEventHandler, WorkflowSchedulingProcessManager>();
        builder.Services.AddScoped<IEventHandler, ExecutionLifecycleNotifier>();

        // Execution limits (loop ceiling, continue-as-new threshold)
        builder.Services.Configure<ExecutionLimitsOptions>(
            builder.Configuration.GetSection(ExecutionLimitsOptions.SectionName));

        // Add Hosted Services
        builder.Services.AddHostedService<WorkflowExecutionEventProcessor>();

        return builder;
    }

    /// <summary>
    /// Auto-apply migrations on startup (since the database is ephemeral).
    /// </summary>
    public static WebApplication UseExecutionInfrastructure(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowExecutionDbContext>();
        dbContext.Database.Migrate();

        return app;
    }
}
