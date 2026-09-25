using Common.SeedWork;
using Execution.Domain.Services;
using Execution.Infrastructure.Dapr.ActivityHandlers;
using Execution.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace UnitTests.Architecture;

/// <summary>
/// The workflow HTTP client must be the one <c>ExecuteActivity</c> actually resolves.
///
/// This exists because the failure mode is silent. Registering the handler twice — once as
/// <c>AddScoped&lt;IActivityHandler, HttpActivityHandler&gt;</c> and once as
/// <c>AddHttpClient&lt;HttpActivityHandler&gt;</c> — compiles, resolves, and passes every other test
/// in the suite, while the interface resolution quietly binds to the default unnamed
/// <see cref="HttpClient"/> and the egress guard and timeout attach to a named client nothing ever
/// asks for. Nothing fails; the controls simply stop applying.
/// </summary>
public class WorkflowHttpClientRegistrationTests
{
    private static IServiceCollection Registered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddWorkflowHttpClient();

        // The handler needs blob access, which the *blobs* module provides — modules do not
        // reference each other, so the execution module registers the consumer and not the
        // implementation. Stubbed here because what is under test is the HTTP client's wiring; that
        // the real one is registered is `AddBlobsModule`'s business.
        builder.Services.AddSingleton<Common.Blobs.IBlobAccess, NoBlobs>();

        return builder.Services;
    }

    private sealed class NoBlobs : Common.Blobs.IBlobAccess
    {
        public long InlineThreshold => long.MaxValue;

        public bool CanStore => false;

        public Task<Common.Workflow.WorkflowBlob> ProduceAsync(
            Stream content, string mediaType, string? name,
            Common.Blobs.BlobUsage provenance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> ReadAsync(
            Common.Workflow.WorkflowBlob blob, ExecutionId runTree, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PromoteAsync(
            IReadOnlyCollection<Common.Workflow.WorkflowBlob> blobs, ExecutionId runTree,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void The_handler_is_registered_exactly_once()
    {
        // Two registrations is the shape of the bug: the second is what ExecuteActivity resolves,
        // and it is not the configured one.
        Assert.Single(Registered(), d => d.ServiceType == typeof(IActivityHandler));
    }

    [Fact]
    public void The_handler_is_built_by_the_http_client_factory_not_the_container()
    {
        var descriptor = Assert.Single(
            Registered(), d => d.ServiceType == typeof(IActivityHandler));

        // A typed client is constructed by a factory that hands it the configured named client.
        // An ImplementationType here would mean the container builds it directly and injects
        // whatever HttpClient it can find, which is the default one.
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
    }

    [Fact]
    public void Resolving_the_interface_yields_the_http_activity_handler()
    {
        // Exactly what ExecuteActivity does.
        using var provider = Registered().BuildServiceProvider();

        var handler = Assert.Single(provider.GetServices<IActivityHandler>());

        Assert.IsType<HttpActivityHandler>(handler);
    }

    [Fact]
    public void The_named_client_the_typed_registration_uses_has_handlers_attached()
    {
        // Note the timeout is deliberately NOT on HttpClient.Timeout — it is a Polly strategy, so
        // that it raises TimeoutRejectedException rather than the TaskCanceledException a real
        // cancellation also raises. So the client's own Timeout proves nothing; what proves the
        // configuration landed is that the named client has a handler pipeline at all.
        using var provider = Registered().BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(nameof(IActivityHandler));

        Assert.NotEmpty(options.HttpMessageHandlerBuilderActions);
    }

    [Fact]
    public async Task The_resolved_handler_refuses_the_cloud_metadata_address()
    {
        // The decisive one, and behavioural rather than structural: it can only pass if the
        // handler ExecuteActivity resolves is the typed client, that client's primary handler is
        // the SocketsHttpHandler configured above, and its ConnectCallback is the egress guard.
        // Any break in that chain and the request is simply attempted.
        //
        // Nothing is dialled — the callback rejects before a socket is opened.
        using var provider = Registered().BuildServiceProvider();

        var handler = Assert.Single(provider.GetServices<IActivityHandler>());

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            handler.ExecuteAsync(new ActivityHandlerInput(
                ExecutionId.New(), ExecutionId.New(),
                "http://169.254.169.254/latest/meta-data/", "GET", null, null)));

        Assert.Contains("blocked", Describe(failure), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The message, plus any inner ones — the guard's refusal is wrapped in transit.</summary>
    private static string Describe(Exception exception)
    {
        var text = new System.Text.StringBuilder();

        for (var current = exception; current != null; current = current.InnerException)
            text.Append(current.Message).Append(' ');

        return text.ToString();
    }
}
