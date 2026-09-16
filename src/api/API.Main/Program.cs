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

builder.AddExecutionModule(ServiceName.Database.Utos);
builder.AddDefinitionModule();
builder.AddObservabilityModule();

var app = builder.Build();

app.UseExecutionModule();
app.UseDefinitionModule();
app.UseObservabilityModule();

app.Run();
