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
using Microsoft.Extensions.Options;
using Polly;
using System.Net.Http;
using Execution.Application.Models;
using Execution.Application.Repositories;
using Execution.Application.Services;
using Execution.Domain.Events;
using Execution.Domain.Interpreter;
using Execution.Domain.Schemas;
using Execution.Infrastructure.Schemas;
using Execution.Domain.Services;
using Execution.Domain.Templating;
using Execution.Infrastructure.Context;
using Execution.Infrastructure.Dapr;
using Execution.Infrastructure.Dapr.Activities;
using Execution.Infrastructure.Dapr.ActivityHandlers;
using Execution.Infrastructure.Data;
using Execution.Infrastructure.EventProcessing;
using Execution.Infrastructure.Http;
using Execution.Infrastructure.Persistence;
using Execution.Infrastructure.EventHandling;
using Execution.Infrastructure.Projections;
using Execution.Infrastructure.Repositories;
using Execution.Infrastructure.Templating;
using Npgsql;

namespace Execution.Infrastructure.Extensions;

/// <summary>
/// Everything the execution module needs to run, minus its service surface: persistence, the
/// Dapr driver, projections, and the application layer's own registrations. The gRPC endpoints
/// are added by <c>Execution.Api</c>, which composes this.
/// </summary>
public static class ExecutionInfrastructureExtensions
{
    public static IHostApplicationBuilder AddExecutionInfrastructure(this IHostApplicationBuilder builder,
        string databaseName)
    {
        // Configure System.Text.Json globally for Dapr and other serialization
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new ObjectDictionaryJsonConverter());
        });

        // One database. The data source is shared by EF (the read models), the event store and the
        // notification listener; Aspire's registration adds its health check and telemetry.
        builder.AddNpgsqlDataSource(databaseName);

        builder.Services.AddDbContext<WorkflowExecutionDbContext>((provider, options) =>
        {
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>());
            options.AddInterceptors(new AuditableEntityInterceptor());
        });

        // The event store, and the LISTEN connection every wait on it is woken by.
        builder.Services.AddSingleton<EventNotifications>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<EventNotifications>());
        builder.Services.AddSingleton<PostgresEventStore>();

        // Add EventStore Serializer, over the events this module declares. Any IDomainEvent in
        // that assembly without an [EventType] fails here rather than being written under a name
        // no reader resolves.
        builder.Services.AddEventStoreSerialization(typeof(WorkflowExecutionCreatedEvent).Assembly);

        // Add repositories
        builder.Services.AddSingleton<IEventStoreRepository>(provider => provider.GetRequiredService<PostgresEventStore>());
        builder.Services.AddScoped<IWorkflowExecutionRepository, WorkflowExecutionRepository>();

        // The single path by which anything writes an execution's lifecycle
        builder.Services.AddScoped<ExecutionLifecycleWriter>();

        // Reclaims sub-workflow output streams and contexts, from both the terminal transition
        // and cancellation.
        builder.Services.AddScoped<ExecutionSubtreeReclaimer>();

        // The expression engine (api/docs/template-expressions.md) and the limits it runs under.
        builder.Services.Configure<TemplateEngineOptions>(
            builder.Configuration.GetSection(TemplateEngineOptions.SectionName));
        // How a stored blob's bytes reach an expression. Registered here and resolved as nullable by
        // the engine, so a daemon with no blob store still evaluates every expression that does not
        // read one.
        builder.Services.AddSingleton<IBlobReader, BlobAccessReader>();
        builder.Services.AddSingleton<ITemplateEngine, JintTemplateEngine>();

        // Applies a workflow's declared schemas to the data crossing a boundary. A singleton
        // because it caches compiled schemas, and safe as one because it holds no per-run state
        // and is pure given the schema and the value.
        builder.Services.AddSingleton<ISchemaValidator, JsonSchemaValidator>();

        // Add the workflow interpreter (pure decision logic, no Dapr/EF)
        builder.Services.AddSingleton<StructRenderer>();
        builder.Services.AddSingleton<WorkflowInterpreter>();

        // Add Execution Context Store
        builder.Services.AddScoped<IExecutionContextStore, EfCoreExecutionContextStore>();

        // Add the orchestration port, so cancelling a run does not pull Dapr into the
        // application layer.
        builder.Services.AddScoped<IExecutionRuntime, DaprExecutionRuntime>();

        // Add the execution output stream — the durable log of what a workflow produced.
        builder.Services.AddSingleton<IExecutionOutputStore, PostgresExecutionOutputStore>();

        builder.AddWorkflowHttpClient();

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
            options.RegisterActivity<AppendOutputEntryActivity>();
            options.RegisterActivity<DeleteOutputStreamActivity>();
            options.RegisterActivity<RaiseChildOutputActivity>();
            options.RegisterActivity<AckChildOutputActivity>();
            options.RegisterActivity<CancelChildExecutionActivity>();
            options.RegisterActivity<ReadChildFailureActivity>();
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
    /// The HTTP client workflow activities make their requests with, and the policy around it:
    /// where it may connect, how long a request may take, and that it does not retry.
    ///
    /// Separate from the rest of the module so it can be registered — and asserted — without a
    /// database, an event store or a Dapr sidecar. That matters more here than elsewhere: the
    /// failure mode this shape exists to prevent is silent.
    /// </summary>
    public static TBuilder AddWorkflowHttpClient<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        // Outbound HTTP policy for workflow activities: which destinations are reachable, and
        // how long a request may take.
        builder.Services.Configure<HttpEgressOptions>(
            builder.Configuration.GetSection(HttpEgressOptions.SectionName));
        builder.Services.AddSingleton<HttpEgressGuard>();

        // Add Activity Handlers (strategy pattern for activity type dispatch)
        //
        // Registered as a typed client *against the interface*, which is the only thing that
        // resolves: ExecuteActivity injects IEnumerable<IActivityHandler>. Registering the
        // handler separately (AddScoped<IActivityHandler, …> plus AddHttpClient<Handler>) built
        // it from the default unnamed HttpClient and left every bit of configuration below
        // attached to a named client nothing ever asked for — silently inert.
        // What bounds a response body. Bound from configuration so a deployment can raise or lower
        // it; the default is what keeps a runaway endpoint from deciding how much memory the daemon
        // uses, since every body under it becomes an inline blob.
        builder.Services.Configure<HttpActivityOptions>(
            builder.Configuration.GetSection(HttpActivityOptions.SectionName));

        var workflowHttp = builder.Services.AddHttpClient<IActivityHandler, HttpActivityHandler>();

        // Drop the standard pipeline inherited from ConfigureHttpClientDefaults. Its retry
        // strategy is wrong here specifically: a workflow's POST may create an order or charge a
        // card, the daemon has no idempotency key to make that safe, and after on_failure routing
        // the author is the one who decides whether to retry. Adding a second handler instead of
        // removing this one would stack two pipelines rather than replace one.
        //
        // EXTEXP0001: RemoveAllResilienceHandlers is still marked experimental upstream. It is the
        // only supported way to drop an inherited pipeline for one client, and the alternative —
        // not applying resilience globally at all — is precisely what this change undoes. If it is
        // ever withdrawn, the fallback is to stop using ConfigureHttpClientDefaults for resilience
        // and opt each client in individually.
#pragma warning disable EXTEXP0001
        workflowHttp.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        workflowHttp.AddResilienceHandler("workflow-http", (pipeline, context) =>
        {
            var egress = context.ServiceProvider
                .GetRequiredService<IOptions<HttpEgressOptions>>().Value;

            // Timeout only. Polly's strategy rather than HttpClient.Timeout, so a timeout surfaces
            // as TimeoutRejectedException instead of the TaskCanceledException a real cancellation
            // also raises — the two need telling apart now that CancelExecution exists.
            pipeline.AddTimeout(egress.RequestTimeout);
        });

        // Egress policy runs at connect time, not as a DelegatingHandler: auto-redirect is on by
        // default, so a message handler never sees a 302 into an internal address, and validating
        // a hostname then handing the hostname to the socket leaves a rebinding window. A connect
        // callback resolves and dials the same approved address.
        workflowHttp.ConfigurePrimaryHttpMessageHandler(serviceProvider => new SocketsHttpHandler
        {
            ConnectCallback = serviceProvider.GetRequiredService<HttpEgressGuard>().ConnectAsync
        });

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
