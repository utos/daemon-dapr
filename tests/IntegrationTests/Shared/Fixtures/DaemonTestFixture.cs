using System.Diagnostics;
using System.Text.RegularExpressions;
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
    public BlobService.BlobServiceClient BlobClient { get; private set; } = null!;
    public WireMockServer WireMockServer { get; private set; } = null!;
    public string MockBaseUrl => WireMockServer.Urls[0];

    /// <summary>
    /// The run's database, for a test that reads or writes the daemon's own tables directly.
    /// <para>
    /// Allocated per run, so it cannot be a constant. Used by tests whose subject is a repository
    /// rather than the gRPC surface — where going through the daemon would test the transport as
    /// well and say less about the thing under test.
    /// </para>
    /// </summary>
    public string DatabaseConnectionString { get; private set; } = null!;

    /// <summary>
    /// The runtime the daemon requires, asserted before anything starts.
    /// <para>
    /// Below this, an <c>on_emitted</c> handler dispatched after a continue-as-new collides with
    /// the still-live producer's id and Dapr drops the creation without faulting the caller — so
    /// the consumer tests do not fail, they <em>hang</em>, once per test, for the full stream
    /// deadline. That is a slow and thoroughly misleading way to be told to upgrade, which is why
    /// this is a precondition rather than a note in the README.
    /// </para>
    /// </summary>
    private static readonly Version MinimumDaprRuntime = new(1, 18, 4);

    public async Task InitializeAsync()
    {
        AssertDaprRuntimeIsSupported();

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

        DatabaseConnectionString = await App.GetConnectionStringAsync(ServiceName.Database.Utos)
            ?? throw new InvalidOperationException(
                $"'{ServiceName.Database.Utos}' has no connection string.");

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
        BlobClient = new BlobService.BlobServiceClient(invoker);

        await WaitUntilDaemonAnswersAsync(TimeSpan.FromSeconds(60));

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
    /// Reads the runtime version from the Dapr CLI, which is the binary
    /// <c>WithDaprSidecar()</c> will launch.
    /// <para>
    /// Deliberately lenient about everything except a version it can read and compare: no CLI on
    /// PATH, unexpected output, an edge or custom build — all pass. The check exists to catch the
    /// one case worth catching, an ordinary install that is simply too old, and a precondition
    /// that fails on unfamiliar-but-fine setups would be worse than none.
    /// </para>
    /// </summary>
    private static void AssertDaprRuntimeIsSupported()
    {
        string output;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("dapr", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            });

            if (process == null) return;

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // No CLI, not on PATH, not executable. Not this method's business.
            return;
        }

        var match = Regex.Match(output, @"Runtime version:\s*(\d+)\.(\d+)\.(\d+)");
        if (!match.Success) return;

        var installed = new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));

        if (installed >= MinimumDaprRuntime) return;

        throw new InvalidOperationException(
            $"Dapr runtime {installed} is installed; the daemon requires {MinimumDaprRuntime} or "
            + "later. On earlier runtimes an on_emitted handler dispatched after a continue-as-new "
            + "collides with the live producer's id, and the creation is dropped without faulting "
            + "the caller — so the consumer tests hang rather than fail. "
            + $"Run: dapr uninstall && dapr init --runtime-version {MinimumDaprRuntime}");
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

    /// <summary>
    /// Waits until the daemon answers a call, which is when it has finished starting.
    /// <para>
    /// <see cref="KnownResourceStates.Running"/> only says the process was launched; the daemon then
    /// still migrates its databases and starts its hosted services before Kestrel listens. A test
    /// class that never calls the daemon is over within a second, so without this the run's
    /// teardown stopped a daemon still starting — the database going away under its migrations, or
    /// the stop cancelling its start-up — and it aborted, leaving a core dump in its directory.
    /// </para>
    /// </summary>
    private async Task WaitUntilDaemonAnswersAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                await DefinitionClient.ListWorkflowsAsync(
                    new ListWorkflowsRequest(), deadline: DateTime.UtcNow.AddSeconds(5));
                return;
            }
            catch (Grpc.Core.RpcException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    public async Task DisposeAsync()
    {
        WireMockServer.Stop();
        await App.DisposeAsync();
    }
}
