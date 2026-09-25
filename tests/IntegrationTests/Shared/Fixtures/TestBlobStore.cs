using Common.Blobs;
using Common.Blobs.S3;
using Common.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntegrationTests.Shared.Fixtures;

/// <summary>
/// The blob store driver, built from the same connection string the daemon is given.
/// <para>
/// Built here rather than resolved out of the daemon's container: a test that reached into another
/// process's DI to get at the driver would prove less and break more. And its bucket is created here
/// rather than waited for, because the daemon's own initializer runs during startup and Aspire reports
/// the daemon <c>Running</c> when Kestrel answers, which is not ordered against it — a test that
/// borrowed that provisioning would fail for a reason it is not about.
/// </para>
/// </summary>
public static class TestBlobStore
{
    public static IObjectStore For(DaemonTestFixture fixture)
    {
        var options = OptionsFor(fixture);

        var client = S3ObjectStoreExtensions.CreateClient(options);

        try
        {
            client.PutBucketAsync(
                new Amazon.S3.Model.PutBucketRequest { BucketName = options.Bucket },
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Already there, which is the usual case.
        }

        return new S3ObjectStore(client, new OptionsWrapper<S3ObjectStoreOptions>(options));
    }

    /// <summary>The store's address, from the connection string the Aspire model hands out.</summary>
    public static S3ObjectStoreOptions OptionsFor(DaemonTestFixture fixture)
    {
        var connectionString = fixture.App.Services
            .GetRequiredService<Aspire.Hosting.ApplicationModel.DistributedApplicationModel>()
            .Resources
            .OfType<Aspire.Hosting.ApplicationModel.IResourceWithConnectionString>()
            .Single(resource => resource.Name == ServiceName.Storage.Blobs)
            .GetConnectionStringAsync(CancellationToken.None)
            .GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The blob store has no connection string.");

        var options = new S3ObjectStoreOptions();
        options.ApplyConnectionString(connectionString);

        return options;
    }
}
