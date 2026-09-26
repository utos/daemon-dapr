using Blobs.Domain;

namespace Blobs.Application;

/// <summary>How the blobs module is configured (<c>api/docs/binary-data.md</c> § Limits).</summary>
public sealed class BlobOptions
{
    public const string SectionName = "Blobs";

    /// <summary>
    /// The largest blob carried inline. Above it the bytes go to the store and the value becomes a
    /// handle.
    /// <para>
    /// A few hundred kibibytes, as the spec advises, and the reasoning is durability rather than
    /// memory: every inline byte is copied at every hop and written to history, and in this daemon a
    /// response reaches the interpreter as a Dapr activity input — so an inline body is serialized
    /// into the orchestration history on every transition of the run. A megabyte-scale threshold
    /// would quietly turn one large response into tens of megabytes of durable state.
    /// </para>
    /// <para>
    /// Invisible to authors either way: no document can tell which backing a blob has, which is what
    /// makes this a deployment's choice rather than part of the contract.
    /// </para>
    /// </summary>
    public long InlineThresholdBytes { get; set; } = 256 * 1024;

    /// <summary>How often the sweep runs. Zero or less turns it off, which is a supported deployment.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many blobs one query of the sweep considers, and so how big one transaction is.</summary>
    public int SweepBatchSize { get; set; } = 200;

    /// <summary>
    /// How many batches one pass will do before leaving the rest to the next interval. A first sweep
    /// after a long outage has an unbounded amount of work, and a pass that tried to finish it would
    /// hold a connection for hours.
    /// </summary>
    public int SweepMaxBatches { get; set; } = 25;

    /// <summary>§ When intermediates go. See <see cref="BlobRetentionPolicy"/> for what each means.</summary>
    public TimeSpan FailedRunWindow { get; set; } = BlobRetentionPolicy.Default.FailedRunWindow;

    public TimeSpan AbandonedUploadWindow { get; set; } = BlobRetentionPolicy.Default.AbandonedUploadWindow;

    public TimeSpan InterruptedRunWindow { get; set; } = BlobRetentionPolicy.Default.InterruptedRunWindow;

    public TimeSpan SweepGracePeriod { get; set; } = BlobRetentionPolicy.Default.GracePeriod;

    public BlobRetentionPolicy RetentionPolicy() =>
        new(FailedRunWindow, AbandonedUploadWindow, InterruptedRunWindow, SweepGracePeriod);

    /// <summary>
    /// How long a signed upload or download location stays valid (§ Limits).
    /// <para>
    /// Short on purpose. A location is a pre-authorised request against the store, so its
    /// lifetime is how long a leaked one is useful; a client that needs longer re-signs, which
    /// costs one call and is what <c>SignUploadParts</c> exists to make cheap.
    /// </para>
    /// </summary>
    public TimeSpan LocationExpiry { get; set; } = TimeSpan.FromMinutes(15);
}
