using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Common.Blobs.S3;

/// <summary>
/// <see cref="IObjectStore"/> over the S3 API — MinIO in development, any S3-compatible server or
/// real S3 in a deployment.
/// <para>
/// One driver for all of them, deliberately. The spec's § Storage requirements are the S3 API's
/// guarantees, so a second driver for the development store would be a second code path exercised
/// only in tests, and the first difference between them would be found in production.
/// </para>
/// </summary>
public sealed class S3ObjectStore(IAmazonS3 client, IOptions<S3ObjectStoreOptions> options)
    : IObjectStore
{
    private readonly S3ObjectStoreOptions _options = options.Value;

    public async Task<ObjectStat> WriteAsync(
        ObjectKey key, Stream content, string mediaType, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            InputStream = content,
            // The caller owns the stream. A spilled response body is read once, by whoever is
            // reading the response, and closing it here would take the rest of that read with it.
            AutoCloseStream = false,
        };

        // Not `DisablePayloadSigning`: that requires HTTPS, and a development store is plain HTTP,
        // so it fails before a byte is sent. Signing stays on and the SDK uses aws-chunked
        // encoding, which is what lets a stream of unknown length — a chunked response has no
        // trustworthy content-length — be signed as it goes rather than buffered to be measured.

        if (!string.IsNullOrEmpty(mediaType))
            request.ContentType = mediaType;

        await Call(() => client.PutObjectAsync(request, cancellationToken), "write", key);

        // The count of bytes the store now holds, asked for rather than assumed: what was written
        // is what decides a blob's size, and the caller counted a stream it did not control.
        return await StatAsync(key, cancellationToken)
               ?? throw new ObjectStoreException(
                   $"'{key}' was written but the store does not hold it.", transient: true);
    }

    public async Task<Stream> OpenReadAsync(
        ObjectKey key, long offset, long length, CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        // An empty window reads nothing rather than asking the store for a range it would refuse:
        // `bytes=0--1` is not a range, and a zero-length blob is ordinary (an empty response body
        // is a blob of size 0).
        if (length == 0) return Stream.Null;

        var request = new GetObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            ByteRange = new Amazon.S3.Model.ByteRange(offset, offset + length - 1),
        };

        var response = await Call(
            () => client.GetObjectAsync(request, cancellationToken), "read", key);

        return response.ResponseStream;
    }

    public async Task<ObjectStat?> StatAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectMetadataAsync(
                _options.Bucket, key.Value, cancellationToken);

            return new ObjectStat(
                response.ContentLength,
                response.Headers.ContentType ?? string.Empty,
                response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            throw Failed("stat", key, ex);
        }
    }

    public async Task DeleteAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(_options.Bucket, key.Value, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone. A retention sweep runs more than once over the same wreckage, and one
            // that threw on an object a previous pass took would never finish.
        }
        catch (AmazonS3Exception ex)
        {
            throw Failed("delete", key, ex);
        }
    }

    public Task<SignedLocation> SignUploadAsync(
        ObjectKey key, string mediaType, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expiry),
            Protocol = Protocol,
        };

        if (!string.IsNullOrEmpty(mediaType))
            request.ContentType = mediaType;

        var url = client.GetPreSignedURL(request);

        // The content type is signed into the URL, so a client that omits the header is refused
        // by the store. It travels beside the URL rather than being left for the client to guess.
        var headers = string.IsNullOrEmpty(mediaType)
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["content-type"] = mediaType };

        return Task.FromResult(
            new SignedLocation(url, headers, DateTimeOffset.UtcNow.Add(expiry)));
    }

    public Task<SignedLocation> SignDownloadAsync(
        ObjectKey key, Common.Blobs.ByteRange? range, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry),
            Protocol = Protocol,
        });

        // A handle that covers part of an object is downloaded as a range. The header is the
        // client's to send — it is not signed, so it stays a header rather than part of the URL.
        var headers = range is { } window && window.Length > 0
            ? new Dictionary<string, string>
            {
                ["range"] = $"bytes={window.Offset}-{window.Offset + window.Length - 1}",
            }
            : new Dictionary<string, string>();

        return Task.FromResult(
            new SignedLocation(url, headers, DateTimeOffset.UtcNow.Add(expiry)));
    }

    /// <summary>
    /// The scheme a signed URL is issued in. <c>AmazonS3Config.UseHttp</c> governs the SDK's own
    /// calls but not <c>GetPreSignedURL</c>, which defaults to HTTPS regardless — so a development
    /// store on plain HTTP handed out https locations, and a client replaying one got a TLS
    /// handshake against a plaintext port rather than an upload.
    /// </summary>
    private Amazon.S3.Protocol Protocol =>
        _options.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Amazon.S3.Protocol.HTTP
            : Amazon.S3.Protocol.HTTPS;

    private static async Task<T> Call<T>(Func<Task<T>> call, string what, ObjectKey key)
    {
        try
        {
            return await call();
        }
        catch (AmazonS3Exception ex)
        {
            throw Failed(what, key, ex);
        }
    }

    /// <summary>
    /// Whether the caller should retry or give up. The spec draws that line — a transient failure
    /// is retried as any activity-level I/O is, and only becomes <c>UTOS-F103</c> once the
    /// implementation gives up — so the driver answers it rather than leaving every caller to
    /// classify an <c>AmazonS3Exception</c> for itself.
    /// </summary>
    private static ObjectStoreException Failed(string what, ObjectKey key, AmazonS3Exception ex)
    {
        var transient =
            ex.StatusCode is System.Net.HttpStatusCode.RequestTimeout
                or System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.InternalServerError
                or System.Net.HttpStatusCode.BadGateway
                or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.GatewayTimeout;

        return new ObjectStoreException(
            $"Could not {what} '{key}': {ex.Message}", transient, ex);
    }
}
