namespace Common.Blobs.S3;

/// <summary>
/// What the S3 driver needs to reach a store. Populated from the <c>blobs</c> connection string
/// Aspire hands over — <c>Endpoint=http://host:port;AccessKey=…;SecretKey=…</c> — or from
/// configuration in a deployment, where the endpoint is empty and the region names real S3.
/// </summary>
public sealed class S3ObjectStoreOptions
{
    public const string SectionName = "Blobs";

    /// <summary>
    /// The S3 endpoint, for a store that is not AWS: MinIO in development, or any S3-compatible
    /// server. Empty means real S3 at <see cref="Region"/>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// The bucket every object goes in. One bucket, not one per run or per tenant: which blobs
    /// exist and who may read them is the daemon's record, and a bucket is not an access boundary
    /// the spec recognises (§ Access).
    /// </summary>
    public string Bucket { get; set; } = "utos-blobs";

    /// <summary>
    /// Create <see cref="Bucket"/> at startup when it is missing. True for a development store
    /// that starts empty; a deployment provisions its bucket and should leave this off, so that a
    /// typo in the name fails loudly instead of quietly filling a new bucket.
    /// </summary>
    public bool CreateBucketIfMissing { get; set; }

    /// <summary>
    /// Path-style addressing (<c>host/bucket/key</c>) rather than virtual-host style
    /// (<c>bucket.host/key</c>). Required by MinIO and by most S3-compatible servers, which have
    /// no wildcard DNS; real S3 wants it off.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>How long a signed upload or download location stays valid (§ Limits).</summary>
    public TimeSpan LocationExpiry { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Reads the Aspire form, <c>Endpoint=…;AccessKey=…;SecretKey=…</c>. Unknown keys are ignored
    /// rather than refused: this is a connection string a host wrote, not a document a user did,
    /// and a driver that fails to start because the host added a field is worse than one that
    /// takes what it recognises.
    /// </summary>
    public void ApplyConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;

            switch (pair[0].Trim().ToLowerInvariant())
            {
                case "endpoint":
                    Endpoint = pair[1];
                    break;
                case "accesskey":
                    AccessKey = pair[1];
                    break;
                case "secretkey":
                    SecretKey = pair[1];
                    break;
                case "bucket":
                    Bucket = pair[1];
                    break;
                case "region":
                    Region = pair[1];
                    break;
            }
        }
    }

    /// <summary>
    /// Whether this names a store at all. A daemon with no store is a supported configuration —
    /// inline blobs work, anything larger is <c>UTOS-F102</c> — so the absence is answered here
    /// rather than thrown about at startup.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
