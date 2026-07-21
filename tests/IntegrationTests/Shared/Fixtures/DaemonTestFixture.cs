using Aspire.Hosting;
using Common.Constants;
using Grpc.Net.Client;
using IntegrationTests.Shared.Builders;
using Utos.Daemon.V1;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace IntegrationTests.Shared.Fixtures;

/// <summary>
/// Shared test fixture for all integration tests.
/// Provides configured daemon client and WireMock server.
/// </summary>
public class DaemonTestFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;
    public ExecutionService.ExecutionServiceClient DaemonClient { get; private set; } = null!;
    public DefinitionService.DefinitionServiceClient DefinitionClient { get; private set; } = null!;
    public ObservabilityService.ObservabilityServiceClient ObservabilityClient { get; private set; } = null!;
    public WireMockServer WireMockServer { get; private set; } = null!;
    public string MockBaseUrl => WireMockServer.Urls[0];

    public async Task InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Tools_AppHost>();
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        // Allow the ExecutionLog provider to receive Debug-level logs so that WatchExecution
        // clients can observe HTTP request details emitted by activities during test runs.
        appHost.Resources
            .Single(r => r.Name == ServiceName.App.MainApi)
            .Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
            {
                ctx.EnvironmentVariables["Logging__ExecutionLog__LogLevel__Default"] = "Debug";

                // Low loop limits so continue-as-new and the runaway ceiling are reachable
                // in a test. Non-looping workflows are unaffected: continuation only kicks
                // in once a back-edge has been detected.
                ctx.EnvironmentVariables["Execution__ContinueAsNewAfterTransitions"] = "5";
                ctx.EnvironmentVariables["Execution__MaxLoopedTransitions"] = "30";
            }));

        App = await appHost.BuildAsync();
        var resourceNotificationService = App.Services.GetRequiredService<ResourceNotificationService>();
        await App.StartAsync();

        await resourceNotificationService.WaitForResourceAsync(ServiceName.App.MainApi, KnownResourceStates.Running)
            .WaitAsync(TimeSpan.FromSeconds(60));

        // Setup gRPC clients
        var httpClient = App.CreateHttpClient(ServiceName.App.MainApi);
        var serviceAddress = httpClient.BaseAddress?.ToString().TrimEnd('/');
        var channel = GrpcChannel.ForAddress(serviceAddress!);
        DaemonClient = new ExecutionService.ExecutionServiceClient(channel);
        DefinitionClient = new DefinitionService.DefinitionServiceClient(channel);
        ObservabilityClient = new ObservabilityService.ObservabilityServiceClient(channel);

        // Setup WireMock
        WireMockServer = WireMockServer.Start();

        // Configure default successful endpoint for tests
        WireMockServer
            .Given(Request.Create().WithPath("/success").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"success\"}"));
    }

    /// <summary>
    /// Loads a test workflow into the daemon's definition store and schedules a run of it.
    /// Since v0.0.10 <c>ScheduleExecution</c> takes a reference rather than a bundle, so
    /// every test goes through this two-step path.
    /// </summary>
    public async Task<ScheduleExecutionResponse> ScheduleAsync(TestWorkflowRequest request)
    {
        var loaded = await DefinitionClient.LoadWorkflowAsync(
            new LoadWorkflowRequest { Bundle = request.Bundle });

        var scheduleRequest = new ScheduleExecutionRequest
        {
            Workflow = loaded.Reference,
            Input = request.Input,
            StartActivity = request.StartActivity
        };

        foreach (var (key, value) in request.Env)
            scheduleRequest.Env.Add(key, value);

        return await DaemonClient.ScheduleExecutionAsync(scheduleRequest);
    }

    public async Task DisposeAsync()
    {
        WireMockServer.Stop();
        await App.DisposeAsync();
    }
}
