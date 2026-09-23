using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Common.Blobs.S3;

/// <summary>
/// Creates the bucket when it is missing, for a development store that starts empty.
/// <para>
/// Off unless <see cref="S3ObjectStoreOptions.CreateBucketIfMissing"/> says otherwise, and a
/// deployment should leave it off: a daemon that creates whatever bucket name it is given turns a
/// typo into a second, empty bucket that silently collects everything, where the same typo against
/// a provisioned bucket fails on the first write and is fixed in a minute.
/// </para>
/// <para>
/// A hosted service rather than work done at the first write, so the cost is paid once at startup
/// instead of being a race every writer has to win.
/// </para>
/// </summary>
public sealed class S3BucketInitializer(
    IAmazonS3 client,
    IOptions<S3ObjectStoreOptions> options,
    ILogger<S3BucketInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bucket = options.Value.Bucket;

        try
        {
            // Create unconditionally and treat "it is already there" as success, rather than
            // asking first. Asking is what broke this: `GetBucketLocation` does not answer 404 for
            // a missing bucket on every S3 implementation, so a pre-check that believed the answer
            // skipped the create and left every write failing with "the specified bucket does not
            // exist". Creating is idempotent on the only answer that matters.
            await client.PutBucketAsync(
                new PutBucketRequest { BucketName = bucket }, cancellationToken);

            logger.LogInformation("Created blob bucket '{BlobBucket}'", bucket);
        }
        catch (AmazonS3Exception ex) when (
            ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Already there — either from a previous run, or because another node won the race,
            // which is the expected outcome of more than one daemon starting against a fresh store.
        }
        catch (AmazonS3Exception ex)
        {
            // Loud, but not fatal. A daemon that cannot make its bucket is misconfigured, and the
            // first write will say so by name; refusing to start would take every workflow that
            // touches no binary data down with it.
            logger.LogError(ex, "Could not create blob bucket '{BlobBucket}'", bucket);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
