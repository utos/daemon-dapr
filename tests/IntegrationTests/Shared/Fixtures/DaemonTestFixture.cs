using Aspire.Hosting;
using Common.Constants;
using Grpc.Net.Client;
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

    public async Task DisposeAsync()
    {
        WireMockServer.Stop();
        await App.DisposeAsync();
    }
}
