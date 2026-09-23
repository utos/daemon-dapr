using Aspire.Hosting.ApplicationModel;
using Common.Constants;
using IntegrationTests.Shared.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests.Storage;

/// <summary>
/// The object storage a blob's bytes live in, as the Aspire model provides it.
/// <para>
/// This asserts the resource is there and answers — not that the daemon uses it, because it does
/// not yet. It exists so the next change starts from a fixture that is known to work: when the
/// blob store lands and a test fails, the question is whether the store is wrong, and that is only
/// a useful question if "MinIO never started" has already been ruled out.
/// </para>
/// <para>
/// Nothing here pins MinIO in particular. What the daemon will depend on is the S3 API, and MinIO
/// is how a container speaks it — so the assertions are about the shape of what Aspire hands over
/// and about the server answering, both of which any S3 implementation must satisfy.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class BlobStorageResourceTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task The_blob_store_is_part_of_the_model_and_hands_over_an_s3_endpoint()
    {
        var resource = Assert.Single(
            fixture.App.Services.GetRequiredService<DistributedApplicationModel>().Resources
                .OfType<IResourceWithConnectionString>()
                .Where(r => r.Name == ServiceName.Storage.Blobs));

        var connectionString = await resource.GetConnectionStringAsync(CancellationToken.None);

        Assert.NotNull(connectionString);

        // The three things an S3 client needs, and the names the daemon's driver will read. Not
        // asserted as one literal: the host and port are allocated per run, so what is contract
        // here is which keys are present, not the string.
        Assert.Contains("Endpoint=http://", connectionString);
        Assert.Contains("AccessKey=", connectionString);
        Assert.Contains("SecretKey=", connectionString);
    }

    [Fact]
    public async Task The_blob_store_is_running_and_answers_its_health_probe()
    {
        // The assertion that would have caught a container that starts and immediately exits —
        // which is the failure a `WaitFor` on it turns into a hung daemon rather than an error.
        var endpoint = Endpoint(
            await fixture.App.Services.GetRequiredService<DistributedApplicationModel>().Resources
                .OfType<IResourceWithConnectionString>()
                .Single(r => r.Name == ServiceName.Storage.Blobs)
                .GetConnectionStringAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("The blob store has no connection string."));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var response = await client.GetAsync(
            new Uri(endpoint, "/minio/health/live"), CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode,
            $"the blob store at {endpoint} answered {(int)response.StatusCode}");
    }

    /// <summary>The <c>Endpoint=</c> part of <c>Endpoint=…;AccessKey=…;SecretKey=…</c>.</summary>
    private static Uri Endpoint(string connectionString)
    {
        var value = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .First(pair => pair[0].Equals("Endpoint", StringComparison.OrdinalIgnoreCase))[1];

        return new Uri(value, UriKind.Absolute);
    }
}
