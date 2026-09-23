using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Common.Blobs.S3;

public static class S3ObjectStoreExtensions
{
    /// <summary>
    /// Registers the blob store, when one is configured.
    /// <para>
    /// Registration is conditional because "no store" is a supported configuration and not a
    /// misconfiguration: inline blobs still work without one, anything that would have to be
    /// stored fails with <c>UTOS-F102</c>, and <c>BlobService</c> answers
    /// <c>FAILED_PRECONDITION</c>. So <see cref="IObjectStore"/> is resolved as a nullable
    /// dependency and its absence is a question callers ask, rather than a startup crash that
    /// would make binary data mandatory for every deployment.
    /// </para>
    /// <para>
    /// Configuration comes from the <c>Blobs</c> section, with the <c>blobs</c> connection string
    /// applied over it — which is what makes the Aspire model work without a settings file, since
    /// the endpoint and credentials there are allocated per run.
    /// </para>
    /// </summary>
    public static IHostApplicationBuilder AddBlobObjectStore(this IHostApplicationBuilder builder)
    {
        var options = new S3ObjectStoreOptions();
        builder.Configuration.GetSection(S3ObjectStoreOptions.SectionName).Bind(options);
        options.ApplyConnectionString(builder.Configuration.GetConnectionString(ConnectionName));

        if (!options.IsConfigured)
            return builder;

        builder.Services.AddSingleton<IOptions<S3ObjectStoreOptions>>(
            new OptionsWrapper<S3ObjectStoreOptions>(options));

        builder.Services.AddSingleton<IAmazonS3>(_ => CreateClient(options));
        builder.Services.AddSingleton<IObjectStore, S3ObjectStore>();

        if (options.CreateBucketIfMissing)
            builder.Services.AddHostedService<S3BucketInitializer>();

        return builder;
    }

    /// <summary>
    /// The connection-string name the daemon reads, and the Aspire resource name beside it. Kept
    /// here rather than in <c>Common.Constants</c>'s <c>ServiceName</c> only because this project
    /// must stay free of a dependency that the AppHost also takes.
    /// </summary>
    public const string ConnectionName = "blobs";

    /// <summary>
    /// An S3 client for <paramref name="options"/>. Public because the shape of this — path-style
    /// addressing, an explicit service URL for a store that is not AWS — is exactly what a test
    /// pointed at the same store must get right, and a second copy of it would be a second place
    /// to get it wrong.
    /// </summary>
    public static AmazonS3Client CreateClient(S3ObjectStoreOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
        };

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.Endpoint;

            // Setting ServiceURL alone is not enough: the SDK still signs and, worse, *presigns*
            // for https, so a development store on plain HTTP hands clients URLs that fail the TLS
            // handshake. The scheme the endpoint was given in is the one to use.
            config.UseHttp = options.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }

        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }
}
