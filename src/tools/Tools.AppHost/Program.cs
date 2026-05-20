using Common.Constants;

var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql");

var workflowExecutionDb = sql.AddDatabase(ServiceName.Database.WorkflowExecution);

var eventstore = builder.AddKurrentDB(ServiceName.App.EventStore)
    .WithEnvironment("EVENTSTORE_ENABLE_ATOM_PUB_OVER_HTTP", "true");

var mainApi = builder
    .AddProject<Projects.API_Main>(ServiceName.App.MainApi)
    .WithReference(eventstore)
    .WaitFor(eventstore)
    .WithReference(workflowExecutionDb)
    .WaitFor(workflowExecutionDb)
    .WithDaprSidecar();

builder.Build().Run();
