using Microsoft.EntityFrameworkCore;

namespace Blobs.Infrastructure.Data;

/// <summary>
/// The blobs module's own tables in the one database. Its own context rather than a shared one:
/// modules do not reference each other, and a context that spanned two of them would be exactly
/// the edge <c>ModuleBoundaryTests</c> forbids.
/// </summary>
public sealed class BlobsDbContext(DbContextOptions<BlobsDbContext> options) : DbContext(options)
{
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();

    public DbSet<BlobRunTreeEntity> BlobRunTrees => Set<BlobRunTreeEntity>();

    public DbSet<BlobHistoryEntity> BlobHistory => Set<BlobHistoryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BlobEntity>(b =>
        {
            b.ToTable("Blobs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(64);
            b.Property(e => e.ObjectKey).IsRequired().HasMaxLength(512);
            b.Property(e => e.State).IsRequired().HasMaxLength(32);
            b.Property(e => e.Retention).IsRequired().HasMaxLength(32);
            b.Property(e => e.Origin).IsRequired().HasMaxLength(32);
            b.Property(e => e.MediaType).IsRequired().HasMaxLength(256);
            b.Property(e => e.Name).HasMaxLength(512);
            b.Property(e => e.ProducerWorkflow).HasMaxLength(512);
            b.Property(e => e.ProducerWorkflowDigest).HasMaxLength(128);
            b.Property(e => e.ProducerActivity).HasMaxLength(256);

            // The retention sweep's query: everything intermediate, oldest first. Without this it
            // is a scan of every blob the daemon has ever held, which is the one table guaranteed
            // to grow.
            b.HasIndex(e => new { e.Retention, e.State, e.CreatedAt });
        });

        modelBuilder.Entity<BlobRunTreeEntity>(b =>
        {
            b.ToTable("BlobRunTrees");

            // The pair *is* the identity: attaching a blob to a tree it already belongs to changes
            // nothing, and the primary key is what makes that true rather than a check-then-insert
            // that two concurrent activities could both pass.
            b.HasKey(e => new { e.BlobId, e.RootExecutionId });

            b.Property(e => e.BlobId).HasMaxLength(64);
            b.Property(e => e.TreeOutcome).HasMaxLength(32);

            // The other direction: everything a run tree brought in, for DeleteExecution and for
            // recording that the tree ended, which is one statement over exactly these rows.
            b.HasIndex(e => e.RootExecutionId);

            // The sweep asks the opposite question — does this blob have an attachment that is not
            // finished — and answers it per candidate blob. Without the ending in the index that is a
            // heap fetch for every row of every candidate, on the one table that grows with every
            // blob a run touches.
            b.HasIndex(e => new { e.BlobId, e.TreeEndedAt });
        });

        modelBuilder.Entity<BlobHistoryEntity>(b =>
        {
            b.ToTable("BlobHistory");
            b.HasKey(e => e.Id);
            b.Property(e => e.BlobId).IsRequired().HasMaxLength(64);
            b.Property(e => e.Kind).IsRequired().HasMaxLength(32);
            b.Property(e => e.Workflow).HasMaxLength(512);
            b.Property(e => e.WorkflowDigest).HasMaxLength(128);
            b.Property(e => e.Activity).HasMaxLength(256);

            // What makes recording an event an upsert. Unique, so two concurrent readers of one
            // blob cannot both insert "read by this workflow" and double the row count; one of
            // them loses the race and folds into the row instead.
            b.HasIndex(e => new { e.BlobId, e.Kind, e.RootExecutionId, e.Workflow }).IsUnique();
        });
    }
}
