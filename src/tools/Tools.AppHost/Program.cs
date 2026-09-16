using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Common.Constants;
using CommunityToolkit.Aspire.Hosting.Dapr;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);

// One database, four roles: Dapr's actor state store, the read models, the event streams and
// the output streams. The daemon gets its connection string from Aspire as usual; the Dapr
// sidecar gets the same database through a `state.postgresql` v2 component, which the Aspire Dapr
// integration cannot write itself. It is written into this directory when the sidecar starts,
// from the endpoint Aspire actually allocated — the integration tests randomise ports, so nothing
// about the endpoint can be static.
var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase(ServiceName.Database.Utos);
var components = Directory.CreateTempSubdirectory("utos-dapr-components-").FullName;

builder
    .AddProject<Projects.API_Main>(ServiceName.App.MainApi)
    .WithReference(database)
    .WaitFor(database)
    .WithDaprSidecar(new DaprSidecarOptions { ResourcesPaths = [components] });

builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(
    new DaprStateStoreOnDatabase(database.Resource, components));

builder.Build().Run();

/// <summary>
/// The actor state store on the one database, for every Dapr sidecar in the model — the same
/// component the compose deployment runs, so what the tests exercise is what is deployed.
/// Two things the Dapr integration does not do on its own: the sidecar is made to wait for the
/// database (a component that cannot connect at init is fatal to daprd, and neither the project's
/// <c>WaitFor</c> nor one on the sidecar builder reaches the executable the sidecar becomes), and
/// the component file is written from the allocated connection string just before it starts.
/// Registered after the integration's own subscriber, so it runs once the sidecars exist.
/// </summary>
sealed class DaprStateStoreOnDatabase(IResourceWithConnectionString database, string componentsPath)
    : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            foreach (var sidecar in @event.Model.Resources.Where(IsSidecar))
                sidecar.Annotations.Add(new WaitAnnotation(database, WaitType.WaitUntilHealthy));

            return Task.CompletedTask;
        });

        eventing.Subscribe<BeforeResourceStartedEvent>(async (@event, token) =>
        {
            if (!IsSidecar(@event.Resource))
                return;

            var connectionString = await database.GetConnectionStringAsync(token)
                ?? throw new InvalidOperationException($"'{database.Name}' has no connection string.");

            await File.WriteAllTextAsync(
                Path.Combine(componentsPath, "statestore.yaml"), Component(connectionString), token);
        });

        return Task.CompletedTask;
    }

    private static bool IsSidecar(IResource resource) =>
        resource is ExecutableResource && resource.Name.EndsWith("-dapr-cli", StringComparison.Ordinal);

    /// <summary>The ADO.NET connection string Aspire hands out, as the libpq DSN daprd wants.</summary>
    private static string Component(string connectionString)
    {
        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

        var dsn = $"host={parts["Host"]} port={parts["Port"]} user={parts["Username"]} " +
                  $"password={parts["Password"]} dbname={parts["Database"]} sslmode=disable";

        return $"""
            apiVersion: dapr.io/v1alpha1
            kind: Component
            metadata:
              name: statestore
            spec:
              type: state.postgresql
              version: v2
              metadata:
                - name: connectionString
                  value: "{dsn}"
                - name: tablePrefix
                  value: dapr_
                - name: actorStateStore
                  value: "true"

            """;
    }
}
