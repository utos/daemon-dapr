using Common.Constants;
using Definition.Infrastructure.Extensions;
using Execution.Infrastructure.Extensions;
using Observability.Infrastructure.Extensions;
using Tools.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Enable Service Defaults
builder.AddServiceDefaults();

builder.AddExecutionModule(ServiceName.Database.WorkflowExecution, ServiceName.App.EventStore);
builder.AddDefinitionModule();
builder.AddObservabilityModule();

// Add services to the container.
// builder.Services.AddControllers();
// builder.Services.AddEndpointsApiExplorer();
// builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment())
// {
//     app.MapOpenApi();
// }

// app.UseHttpsRedirection();

// app.MapControllers();

app.UseExecutionModule();
app.UseDefinitionModule();
app.UseObservabilityModule();

app.Run();
