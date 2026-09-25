using Blobs.Api.Extensions;
using Blobs.Application.Queries;
using Blobs.Domain;
using Common.Blobs;
using Common.SeedWork;
using Utos.Daemon.V1;
using Domain = Blobs.Domain;
using Wire = Utos.Daemon.V1;

namespace UnitTests.Blobs;

/// <summary>
/// Proto ↔ domain for the blob service.
/// <para>
/// Asserted below the gRPC surface, so it needs no Docker — and asserted at all because the enums exist
/// on both sides with different members and a different zero value. The wire has an
/// <c>_UNSPECIFIED</c> that the domain deliberately does not, so a mapping by ordinal would put every
/// member one place out and a mapping by name would compile until somebody adds one.
/// </para>
/// </summary>
public class BlobMappingTests
{
    public class TheEnums
    {
        [Theory]
        [InlineData(Domain.BlobState.Pending, Wire.BlobState.Pending)]
        [InlineData(Domain.BlobState.Ready, Wire.BlobState.Ready)]
        [InlineData(Domain.BlobState.Deleted, Wire.BlobState.Deleted)]
        public void A_state_maps_to_its_own_member(Domain.BlobState domain, Wire.BlobState wire)
        {
            Assert.Equal(wire, domain.ToProto());
        }

        [Theory]
        [InlineData(Domain.BlobRetention.Durable, Wire.BlobRetention.Durable)]
        [InlineData(Domain.BlobRetention.Intermediate, Wire.BlobRetention.Intermediate)]
        public void A_retention_class_maps_to_its_own_member(
            Domain.BlobRetention domain, Wire.BlobRetention wire)
        {
            // The one most worth pinning: the domain's zero is Intermediate and the wire's is
            // UNSPECIFIED, so an ordinal cast would report every intermediate blob as unspecified and
            // every durable one as intermediate — a listing that reads plausibly and is entirely wrong.
            Assert.Equal(wire, domain.ToProto());
        }

        [Theory]
        [InlineData(Domain.BlobOrigin.Uploaded, Wire.BlobOrigin.Uploaded)]
        [InlineData(Domain.BlobOrigin.Produced, Wire.BlobOrigin.Produced)]
        public void An_origin_maps_to_its_own_member(Domain.BlobOrigin domain, Wire.BlobOrigin wire)
        {
            Assert.Equal(wire, domain.ToProto());
        }

        [Theory]
        [InlineData(Domain.BlobEventKind.Uploaded, Wire.BlobEventKind.Uploaded)]
        [InlineData(Domain.BlobEventKind.Produced, Wire.BlobEventKind.Produced)]
        [InlineData(Domain.BlobEventKind.Attached, Wire.BlobEventKind.Attached)]
        [InlineData(Domain.BlobEventKind.Read, Wire.BlobEventKind.Read)]
        [InlineData(Domain.BlobEventKind.Downloaded, Wire.BlobEventKind.Downloaded)]
        public void An_event_kind_maps_to_its_own_member(
            Domain.BlobEventKind domain, Wire.BlobEventKind wire)
        {
            Assert.Equal(wire, domain.ToProto());
        }

        [Fact]
        public void Every_domain_member_is_covered_by_the_cases_above()
        {
            // The guard that outlives this file: a member added to the domain and not to the mapping
            // falls through to `_ => Unspecified`, which compiles, runs, and reports a blob as having no
            // state at all. Counted rather than enumerated, because the assertion is "nothing is
            // missing" and the theories above say what each one is.
            Assert.Equal(3, Enum.GetValues<Domain.BlobState>().Length);
            Assert.Equal(2, Enum.GetValues<Domain.BlobRetention>().Length);
            Assert.Equal(2, Enum.GetValues<Domain.BlobOrigin>().Length);
            Assert.Equal(5, Enum.GetValues<Domain.BlobEventKind>().Length);
        }
    }

    public class TheBlobInfo
    {
        [Fact]
        public void Carries_the_record_and_its_history_s_two_aggregates()
        {
            var info = Summary(Ready()).ToProto();

            Assert.Equal("b_1", info.Id);
            Assert.Equal(Wire.BlobState.Ready, info.State);
            Assert.Equal(Wire.BlobRetention.Durable, info.Retention);
            Assert.Equal(Wire.BlobOrigin.Produced, info.Origin);
            Assert.Equal(1024, info.Size);
            Assert.Equal("text/plain", info.MediaType);
            Assert.Equal(4, info.UsageCount);
            Assert.NotNull(info.LastUsedAt);
        }

        [Fact]
        public void Leaves_absent_what_the_record_does_not_have()
        {
            // Presence is meaning on this message: a blob with no name is a Blob rather than a File, and
            // one with no `deleted_at` has not been deleted. A zero value would read as "at the epoch".
            var info = Unused(Ready() with
            {
                Name = null,
                LastModified = null,
                CompletedAt = null,
                Producer = null,
            }).ToProto();

            Assert.False(info.HasName);
            Assert.Null(info.LastModified);
            Assert.Null(info.CompletedAt);
            Assert.Null(info.DeletedAt);
            Assert.Null(info.LastUsedAt);
            Assert.Null(info.Producer);

            // Never present, and never invented: the proto says `digest` is there only when the daemon
            // hashed the bytes while writing them, and this one streams a body to the store untouched.
            Assert.False(info.HasDigest);
        }

        [Fact]
        public void Splits_the_producer_s_canonical_identity_into_a_name_and_a_version()
        {
            // The record keeps one string so that it survives the execution being deleted. But
            // `WorkflowReference.name` is a name, and handing over "thing:1.0.0" in it would make a
            // client's display wrong and a round trip through it wronger.
            var producer = new Domain.BlobProducer(
                ExecutionId.New(), ExecutionId.New(), "acme/report:2.1.0", "sha256:abc", "fetch");

            var info = Summary(Ready() with { Producer = producer }).ToProto();

            Assert.Equal("acme/report", info.Producer.Workflow.Name);
            Assert.Equal("2.1.0", info.Producer.Workflow.Version);
            Assert.Equal("sha256:abc", info.Producer.Workflow.Digest);
            Assert.Equal("fetch", info.Producer.Activity);
        }

        [Fact]
        public void Answers_an_unparseable_identity_as_a_name_alone()
        {
            // Still the most the daemon knows about what made the blob, which beats dropping it.
            var producer = new Domain.BlobProducer(
                ExecutionId.New(), ExecutionId.New(), "no-version-here");

            var info = Summary(Ready() with { Producer = producer }).ToProto();

            Assert.Equal("no-version-here", info.Producer.Workflow.Name);
            Assert.Empty(info.Producer.Workflow.Version);
        }
    }

    public class AFilter
    {
        [Fact]
        public void Is_empty_when_the_request_sets_nothing()
        {
            // An unset optional must be `null` and not a zero value: `BLOB_RETENTION_UNSPECIFIED` cast
            // to the domain's zero is `Intermediate`, so a listing with no filter would silently return
            // only intermediates — and a prune with none would pass its own emptiness check.
            var filter = new ListBlobsRequest().ToDomain();

            Assert.True(filter.IsEmpty);
            Assert.Null(filter.Retention);
            Assert.Null(filter.Origin);
            Assert.Null(filter.State);
        }

        [Fact]
        public void Carries_every_field_the_request_set()
        {
            var root = ExecutionId.New();
            var before = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

            var filter = new ListBlobsRequest
            {
                Retention = Wire.BlobRetention.Intermediate,
                Origin = Wire.BlobOrigin.Produced,
                State = Wire.BlobState.Ready,
                RootExecutionId = root.ToString(),
                CreatedBefore = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(before),
            }.ToDomain();

            Assert.False(filter.IsEmpty);
            Assert.Equal(Domain.BlobRetention.Intermediate, filter.Retention);
            Assert.Equal(Domain.BlobOrigin.Produced, filter.Origin);
            Assert.Equal(Domain.BlobState.Ready, filter.State);
            Assert.Equal(root, filter.RootExecution);
            Assert.Equal(before, filter.CreatedBefore);
        }

        [Fact]
        public void A_prune_takes_no_state_and_no_run_tree()
        {
            // `PruneBlobsRequest` has neither field. The state is the handler's to decide — a blob
            // already deleted has no bytes to remove — and deleting a run tree's blobs is
            // `DeleteExecution`'s job.
            var filter = new PruneBlobsRequest { Retention = Wire.BlobRetention.Intermediate }.ToDomain();

            Assert.Null(filter.State);
            Assert.Null(filter.RootExecution);
            Assert.Equal(Domain.BlobRetention.Intermediate, filter.Retention);
        }

        [Fact]
        public void An_unfiltered_prune_reads_as_empty_so_the_handler_can_refuse_it()
        {
            Assert.True(new PruneBlobsRequest { DryRun = true }.ToDomain().IsEmpty);
        }
    }

    public class ASignedLocation
    {
        [Fact]
        public void Carries_the_method_and_every_header_verbatim()
        {
            // The headers are not advisory: a store signs a request that includes them, so sending the
            // URL without them is refused. A client replays exactly what it was given.
            var location = new Common.Blobs.SignedLocation(
                "https://store.test/blobs/b_1?sig=x",
                "GET",
                new Dictionary<string, string> { ["range"] = "bytes=0-9" },
                new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

            var proto = location.ToProto();

            Assert.Equal("https://store.test/blobs/b_1?sig=x", proto.Url);
            Assert.Equal("GET", proto.Method);
            Assert.Equal("bytes=0-9", proto.Headers["range"]);
            Assert.Equal(location.ExpiresAt, proto.ExpiresAt.ToDateTimeOffset());
        }
    }

    private static StoredBlob Ready() =>
        new(new BlobId("b_1"),
            new ObjectKey("blobs/b_1"),
            Domain.BlobState.Ready,
            Domain.BlobRetention.Durable,
            Domain.BlobOrigin.Produced,
            "text/plain",
            1024,
            new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            Name: "report.txt",
            LastModified: new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero),
            Producer: new Domain.BlobProducer(
                ExecutionId.New(), ExecutionId.New(), "acme/report:2.1.0", "sha256:abc", "fetch"),
            CompletedAt: new DateTimeOffset(2026, 9, 25, 10, 0, 1, TimeSpan.Zero));

    /// <summary>
    /// A summary with a history behind it. <c>Unused</c> is the other case and is spelled separately
    /// rather than by passing null here, because a default of "some time" and an argument of "never"
    /// through one nullable parameter is exactly how a test ends up asserting the default.
    /// </summary>
    private static BlobSummary Summary(StoredBlob blob) =>
        new(blob, new DateTimeOffset(2026, 9, 25, 11, 0, 0, TimeSpan.Zero), 4);

    private static BlobSummary Unused(StoredBlob blob) => new(blob, null, 0);
}
