using Blobs.Api.Extensions;
using Common.Blobs.S3;
using Common.Application.Behaviors;
using Common.Constants;
using Definition.Api.Extensions;
using Execution.Api.Extensions;
using Mediator;
using Observability.Api.Extensions;
using Tools.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Enable Service Defaults
builder.AddServiceDefaults();

// Cross-cutting application plumbing. Mediator is source-generated here because this is the
// only project that sees every module's handlers; the validation behaviour applies to all of
// them, so no single module can own it either.
builder.Services.AddMediator(options => { options.ServiceLifetime = ServiceLifetime.Scoped; });
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

// The one gRPC surface every module maps its service onto.
builder.Services.AddGrpc();

// Where a stored blob's bytes live (api/docs/binary-data.md § Storage). Registered before the
// modules because more than one of them will reach for it, and registered *conditionally*: a
// daemon with no store configured is a supported deployment, not a broken one — inline blobs
// still work, and anything that would have to be stored is refused by name rather than by crash.
builder.AddBlobObjectStore();

builder.AddBlobsModule(ServiceName.Database.Utos);
builder.AddExecutionModule(ServiceName.Database.Utos);
builder.AddDefinitionModule();
builder.AddObservabilityModule();

var app = builder.Build();

app.UseBlobsModule();
app.UseExecutionModule();
app.UseDefinitionModule();
app.UseObservabilityModule();

try
{
    app.Run();
}
catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
{
    // Told to stop while still starting: the host cancels whatever start-up work is in flight —
    // the bucket being created, Kestrel binding — and Run() rethrows that cancellation. It is the
    // stop that was asked for, not a failure — Ctrl+C on a daemon still coming up, or an
    // orchestrator stopping it early — and left unhandled it aborts the process, which leaves a
    // core dump behind.
}
