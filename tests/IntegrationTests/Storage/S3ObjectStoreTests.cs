using System.Text;
using Common.Blobs;
using Common.Blobs.S3;
using Common.Constants;
using IntegrationTests.Shared.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntegrationTests.Storage;

/// <summary>
/// The blob store driver against a real S3 server — <c>api/docs/binary-data.md</c> § Storage.
/// <para>
/// Against MinIO rather than a fake, because what is being tested is the agreement between this
/// driver and an S3 implementation, and a fake would only test the driver's agreement with my
/// reading of S3. The four things § Storage requires are what is asserted: objects are readable
/// after they are written, reads take a window rather than the whole object, locations can be
/// signed so bytes move without the daemon in the path, and removal is idempotent.
/// </para>
/// <para>
/// The driver is built here from the same connection string the daemon gets, rather than resolved
/// out of the daemon's container: these tests are about the driver, and reaching into another
/// process's DI to get at it would prove less and break more.
/// </para>
/// </summary>
[Collection(DaemonTestCollection.Name)]
public class S3ObjectStoreTests(DaemonTestFixture fixture)
{
    [Fact]
    public async Task An_object_is_readable_after_it_is_written()
    {
        var store = Store();
        var key = Key();
        var bytes = Encoding.UTF8.GetBytes("the quick brown fox");

        var written = await store.WriteAsync(
            key, new MemoryStream(bytes), "text/plain", CancellationToken.None);

        Assert.Equal(bytes.Length, written.Size);
        Assert.Equal("text/plain", written.MediaType);

        await using var stream = await store.OpenReadAsync(
            key, 0, bytes.Length, CancellationToken.None);

        Assert.Equal("the quick brown fox", await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public async Task A_read_takes_the_window_a_handle_describes()
    {
        // What makes slicing a stored blob free: a slice is the same object at a different offset
        // and size, so it must read a range rather than the object.
        var store = Store();
        var key = Key();

        await store.WriteAsync(
            key, new MemoryStream(Encoding.UTF8.GetBytes("0123456789")),
            "application/octet-stream", CancellationToken.None);

        await using var stream = await store.OpenReadAsync(key, 3, 4, CancellationToken.None);

        Assert.Equal("3456", await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public async Task An_empty_window_reads_nothing_without_asking_the_store()
    {
        // A zero-length blob is ordinary — an empty response body is a blob of size 0 — and
        // `bytes=0--1` is not a range any store would accept.
        var store = Store();

        await using var stream = await store.OpenReadAsync(
            new ObjectKey("never-written"), 0, 0, CancellationToken.None);

        Assert.Equal(0, await stream.ReadAsync(new byte[1], CancellationToken.None));
    }

    [Fact]
    public async Task The_size_written_is_the_size_the_store_reports_not_the_one_declared()
    {
        // A spilled response body has no trustworthy content-length, so what the store holds is
        // asked for rather than assumed. This is the same question CompleteBlob asks of a client's
        // upload.
        var store = Store();
        var key = Key();

        await store.WriteAsync(
            key, new MemoryStream(new byte[4096]), "application/octet-stream", CancellationToken.None);

        var stat = await store.StatAsync(key, CancellationToken.None);

        Assert.NotNull(stat);
        Assert.Equal(4096, stat.Size);
    }

    [Fact]
    public async Task Statting_something_the_store_does_not_hold_is_an_answer_not_a_failure()
    {
        Assert.Null(await Store().StatAsync(new ObjectKey(Guid.NewGuid().ToString("n")), CancellationToken.None));
    }

    [Fact]
    public async Task Removing_an_object_twice_succeeds()
    {
        // A retention sweep runs more than once over the same wreckage. One that threw on an
        // object a previous pass already took would never finish.
        var store = Store();
        var key = Key();

        await store.WriteAsync(
            key, new MemoryStream([1, 2, 3]), "application/octet-stream", CancellationToken.None);

        await store.DeleteAsync(key, CancellationToken.None);
        await store.DeleteAsync(key, CancellationToken.None);

        Assert.Null(await store.StatAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task A_client_uploads_and_downloads_at_signed_locations_without_the_daemon()
    {
        // The requirement the whole design rests on: bytes move client-to-store. This test *is* a
        // client — it holds no store credentials and talks to the signed URL with a plain
        // HttpClient, which is exactly what a real client does.
        var store = Store();
        var key = Key();

        var upload = await store.SignUploadAsync(
            key, "text/plain", TimeSpan.FromMinutes(5), CancellationToken.None);

        using var client = new HttpClient();

        var put = new HttpRequestMessage(HttpMethod.Put, upload.Url)
        {
            Content = new StringContent("signed bytes", Encoding.UTF8),
        };

        // Replayed exactly as given: the content type is signed into the URL, so sending the URL
        // without it is refused.
        foreach (var (name, value) in upload.Headers)
        {
            put.Content.Headers.Remove(name);
            put.Content.Headers.TryAddWithoutValidation(name, value);
        }

        var putResponse = await client.SendAsync(put, CancellationToken.None);
        Assert.True(putResponse.IsSuccessStatusCode,
            $"the signed upload answered {(int)putResponse.StatusCode}");

        // The daemon sees the object only after the client put it there.
        var stat = await store.StatAsync(key, CancellationToken.None);
        Assert.NotNull(stat);
        Assert.Equal(12, stat.Size);

        var download = await store.SignDownloadAsync(
            key, null, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal("signed bytes", await client.GetStringAsync(download.Url, CancellationToken.None));
    }

    [Fact]
    public async Task A_signed_download_of_a_partial_handle_carries_the_range_to_send()
    {
        // A handle whose offset and size cover part of an object is downloaded as a range, and the
        // client is told which one rather than left to work it out from the handle.
        var store = Store();
        var key = Key();

        await store.WriteAsync(
            key, new MemoryStream(Encoding.UTF8.GetBytes("0123456789")),
            "application/octet-stream", CancellationToken.None);

        var download = await store.SignDownloadAsync(
            key, new ByteRange(2, 3), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal("bytes=2-4", download.Headers["range"]);

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
        foreach (var (name, value) in download.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("234", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_daemon_creates_its_bucket_on_the_way_up()
    {
        // The daemon-side half, asserted on its own so that a driver test failing means the driver
        // is wrong and not that the store was never provisioned. `CreateBucketIfMissing` is set for
        // the development store by the AppHost.
        //
        // Waited for rather than read once: the bucket is made by a hosted service during startup,
        // and Aspire reports the daemon Running when Kestrel answers — which is not ordered against
        // that service finishing. A single read would be a race, and a racy test that passes most
        // of the time is worse than no test. What this still catches is the failure that matters:
        // a store that was never wired up never creates the bucket at all, and no amount of
        // waiting produces one.
        var options = Options();
        var client = S3ObjectStoreExtensions.CreateClient(options);
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(client, options.Bucket))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        }

        Assert.Fail(
            $"the daemon never created '{options.Bucket}', so its blob store is not configured — "
            + "check that the 'blobs' connection string and Blobs__CreateBucketIfMissing reach it");
    }

    private static ObjectKey Key() => new("test/" + Guid.NewGuid().ToString("n"));

    /// <summary>
    /// The driver, pointed at the run's MinIO by the same connection string the daemon reads.
    /// <para>
    /// The bucket is ensured here rather than assumed from the daemon's startup. These are tests of
    /// the driver, and borrowing another process's provisioning made every one of them fail for a
    /// reason none of them was about — see <c>The_daemon_creates_its_bucket_on_the_way_up</c>,
    /// which is where that particular fact belongs.
    /// </para>
    /// </summary>
    private IObjectStore Store()
    {
        var options = Options();
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

    /// <summary>The store's address, from the same connection string the daemon is given.</summary>
    private S3ObjectStoreOptions Options()
    {
        var connectionString = fixture.App.Services
            .GetRequiredService<Aspire.Hosting.ApplicationModel.DistributedApplicationModel>()
            .Resources
            .OfType<Aspire.Hosting.ApplicationModel.IResourceWithConnectionString>()
            .Single(r => r.Name == ServiceName.Storage.Blobs)
            .GetConnectionStringAsync(CancellationToken.None)
            .GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The blob store has no connection string.");

        var options = new S3ObjectStoreOptions();
        options.ApplyConnectionString(connectionString);
        return options;
    }
}
