using Aspire.Hosting;
using Common.Constants;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// Backstop deadline for a request/response call. Every one of them talks to a daemon on
    /// this machine and should answer in well under a second; this is sized for a loaded box, not
    /// for a slow operation, because there are none.
    /// </summary>
    private static readonly TimeSpan UnaryDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Backstop deadline for a server stream, which legitimately stays open for a whole run.
    /// Above the longest wait any test asks for, so it only ever fires on a stream that has
    /// stopped producing entirely. See <see cref="DeadlineInterceptor"/>.
    /// </summary>
    private static readonly TimeSpan StreamDeadline = TimeSpan.FromSeconds(120);

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

                // A low continuation threshold so a test can span several Dapr instances and
                // prove the cursor survives them. Non-looping workflows are unaffected:
                // continuation only kicks in once a back-edge has been detected.
                ctx.EnvironmentVariables["Execution__ContinueAsNewAfterTransitions"] = "5";
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

        // Every client goes through the deadline interceptor, so no call any test makes can wait
        // forever on a daemon that has stopped answering.
        var invoker = channel.Intercept(new DeadlineInterceptor(UnaryDeadline, StreamDeadline));

        DaemonClient = new ExecutionService.ExecutionServiceClient(invoker);
        DefinitionClient = new DefinitionService.DefinitionServiceClient(invoker);
        ObservabilityClient = new ObservabilityService.ObservabilityServiceClient(invoker);

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

    /// <summary>
    /// Waits for a run to finish, by subscribing rather than polling.
    ///
    /// <see cref="ScheduleAsync"/> returns as soon as the creation event is appended, while
    /// <c>GetExecution</c> and <c>ListExecutions</c> read a projection the event subscription
    /// populates afterwards — so querying straight after scheduling races that projection and
    /// fails intermittently with <c>NOT_FOUND</c>.
    ///
    /// <c>WatchExecution</c>'s stream is closed by an event handler on the terminal event, and
    /// the processor awaits every projection before it runs any handler. The read model is
    /// therefore already current when this returns, which is what makes it a synchronisation
    /// point and not just a wait.
    /// </summary>
    public async Task WatchToCompletionAsync(string executionId, TimeSpan? timeout = null)
    {
        var call = ObservabilityClient.WatchExecution(
            new WatchExecutionRequest { ExecutionId = executionId });

        await call.CollectUntilCompletion(timeout ?? TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        WireMockServer.Stop();
        await App.DisposeAsync();
    }
}
